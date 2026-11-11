using Npgsql;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Payments;
using Revenant.Scenario;
using Revenant.Tests.Ledger;
using LedgerSql = Revenant.Tests.Ledger.Ledger;
using Revenant.Timeline;
using Xunit;
using static Revenant.Tests.Payments.PaymentKit;

namespace Revenant.Tests.Payments;

/// <summary>
/// Spec 07 task 7.2, intake side (R07.02, R07.09, R07.10, R07.12, R07.13): idempotent run-scoped intake, ordered canonical
/// facts, transactional state + outbox, run namespaces and tenant isolation. Real PostgreSQL 18 migrated by
/// <c>scripts/db/migrate.sh</c>, written through the least-privilege writer login.
/// </summary>
[Trait("Category", "Integration")]
[Collection(PaymentCollection.Name)]
public sealed class PaymentIntakeTests(LedgerDatabase db)
{
    private PaymentLedgerStore Store() => PaymentKit.Store(db.Writer);

    [Fact]
    public async Task Migration_0002_applies_and_a_rerun_applies_nothing()
    {
        Assert.Contains("module revenant: applying 0002_payment_intake", db.MigrateOutput);
        var (exit, rerun) = await db.MigrateAgainAsync();
        Assert.Equal(0L, exit);
        Assert.Contains("0 applied", rerun);
    }

    [Fact]
    public async Task Two_equal_seed_runs_coexist_with_identical_opening_balances_in_separate_namespaces()
    {
        var store = Store();
        var runA = await OpenAsync(store, id: new ScenarioRunId(Guid.NewGuid()));
        var a = (await store.GetRunAsync(TenantA, runA, default))!;

        // Real posted traffic in run A: an intake plus a balanced 7.1-style settlement group, so "original ledger" is non-trivial.
        var intentsA = Intents(runA);
        Assert.True((await store.SubmitIntentAsync(TenantA, Operator, intentsA[0], "t", default)).IsSuccess);
        var seeded = await SeedFromDatabaseAsync(runA);
        await LedgerSql.SettleAsync(db.Writer, seeded, "LEGACY-1", seeded.Account("BANK-A/ACC-1"), seeded.Account("BANK-B/ACC-1"), 12_345);
        var runAViewAfterSettlement = (await store.GetRunAsync(TenantA, runA, default))!;
        var before = await Snapshot(runA.Value);

        // Run B: same seed, distinct run id. The same logical bank, account and intent ids are reused on purpose.
        var runB = await OpenAsync(store, id: new ScenarioRunId(Guid.NewGuid()));
        var b = (await store.GetRunAsync(TenantA, runB, default))!;
        Assert.NotEqual(runA, runB);
        Assert.Equal(6, b.Banks.Count);
        Assert.Equal(a.Banks.Select(x => (x.BankId, x.Name, x.OpeningMinor)), b.Banks.Select(x => (x.BankId, x.Name, x.OpeningMinor)));
        Assert.Equal(a.Banks.Select(x => x.OpeningMinor), b.Banks.Select(x => x.PostedMinor));
        Assert.Equal(a.ManifestHash, b.ManifestHash);
        Assert.Equal(18, await Count(db.Owner, "account", runB.Value));
        Assert.Equal(0, await ScalarAsync<long>("SELECT count(*) FROM revenant.account x JOIN revenant.account y ON x.id = y.id AND x.scenario_run_id <> y.scenario_run_id"));
        Assert.Equal(
            0, await ScalarAsync<long>(
                "SELECT count(*) FROM revenant.account WHERE scenario_run_id = @b AND id IN (SELECT id FROM revenant.account WHERE scenario_run_id = @a)",
                ("a", runA.Value), ("b", runB.Value)));

        // The same scenario-local intent id, in both runs, collides on nothing.
        var intentsB = Intents(runB);
        Assert.Equal(intentsA[0].IntentId, intentsB[0].IntentId);
        var intakeB = await store.SubmitIntentAsync(TenantA, Operator, intentsB[0], "t", default);
        Assert.False(intakeB.Value.IsReplay);
        Assert.Equal(2, await ScalarAsync<long>(
            "SELECT count(DISTINCT scenario_run_id) FROM revenant.payment WHERE intent_id = @i AND scenario_run_id IN (@a, @b)",
            ("i", intentsA[0].IntentId), ("a", runA.Value), ("b", runB.Value)));

        // Run A is exactly as it was: nothing deleted or changed, including its balances after the settlement.
        Assert.Equal(before, await Snapshot(runA.Value));
        var aAgain = (await store.GetRunAsync(TenantA, runA, default))!;
        Assert.Equal(runAViewAfterSettlement.Banks, aAgain.Banks);
        Assert.NotEqual(a.Banks.Select(x => x.PostedMinor), aAgain.Banks.Select(x => x.PostedMinor));
        Assert.Equal(b.Banks.Select(x => x.OpeningMinor), (await store.GetRunAsync(TenantA, runB, default))!.Banks.Select(x => x.PostedMinor));

        // No statement the writer (or even the owner) can issue removes a run's history.
        foreach (var table in new[] { "bank", "account", "payment", "ledger_group", "ledger_entry", "finance_fact", "payment_transition", "payment_command" })
        {
            await DeniedForWriterAsync($"DELETE FROM revenant.{table}", "42501");
            var owner = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await using var cmd = db.Owner.CreateCommand($"DELETE FROM revenant.{table}");
                await cmd.ExecuteNonQueryAsync();
            });
            Assert.Equal("RV001", owner.SqlState);
        }
        Assert.Equal(before, await Snapshot(runA.Value));
    }

    [Fact]
    public async Task Reopening_a_run_replays_for_its_owner_conflicts_on_another_seed_and_is_forbidden_to_others()
    {
        var store = Store();
        var run = await OpenAsync(store);
        var replay = await store.OpenRunAsync(TenantA, Operator, "operator", 42, run, default);
        Assert.True(replay.Value.IsReplay);
        Assert.Equal(18, await Count(db.Owner, "account", run.Value));

        var otherSeed = await store.OpenRunAsync(TenantA, Operator, "operator", 43, run, default);
        Assert.Equal(PaymentFailureCode.ConflictingDuplicate, otherSeed.Error.Code);

        var otherOwner = await store.OpenRunAsync(TenantA, "staff:someone-else", "operator", 42, run, default);
        Assert.Equal(PaymentFailureCode.Forbidden, otherOwner.Error.Code);
        var otherTenant = await store.OpenRunAsync(TenantB, Operator, "operator", 42, run, default);
        Assert.Equal(PaymentFailureCode.Forbidden, otherTenant.Error.Code);
        Assert.Equal(1, await Count(db.Owner, "scenario_run_staff", run.Value));
    }

    [Fact]
    public async Task Intake_commits_one_payment_trail_row_fact_command_and_outbox_event_and_replays_without_a_second_effect()
    {
        var store = Store();
        var run = await OpenAsync(store);
        var intent = Intents(run)[0];

        var first = await store.SubmitIntentAsync(TenantA, Operator, intent, "corr-1", default);
        var receipt = first.Value;
        Assert.False(receipt.IsReplay);
        Assert.Equal(1, receipt.FactSequence);
        Assert.Equal(PaymentStatus.Initiated, receipt.Payment.Status);
        Assert.Equal(1, receipt.Payment.Version);
        Assert.Equal(intent.Amount.MinorUnits, receipt.Payment.AmountMinor);
        await AssertEffects(run, payments: 1, transitions: 1, facts: 1, commands: 1, outbox: 1);

        var replay = await store.SubmitIntentAsync(TenantA, Operator, intent, "corr-2", default);
        Assert.True(replay.Value.IsReplay);
        Assert.Equal(receipt.Payment.PaymentId, replay.Value.Payment.PaymentId);
        Assert.Equal(1, replay.Value.FactSequence);
        await AssertEffects(run, payments: 1, transitions: 1, facts: 1, commands: 1, outbox: 1);

        // The stored fact is exactly the canonical intent, and its hash and the event payload are recorded.
        var stored = await ScalarAsync<byte[]>("SELECT canonical FROM revenant.finance_fact WHERE scenario_run_id = @r", ("r", run.Value));
        Assert.Equal(intent, PaymentIntentCodec.Instance.Decode(stored));
        Assert.Equal(FinanceFactCodec.PayloadHash(stored), await ScalarAsync<byte[]>(
            "SELECT canonical_sha256 FROM revenant.finance_fact WHERE scenario_run_id = @r", ("r", run.Value)));
        var eventType = await ScalarAsync<string>(
            "SELECT event_type FROM revenant.outbox WHERE aggregate_id = 'payment:' || replace(@p::text, '-', '')", ("p", receipt.Payment.PaymentId));
        Assert.Equal(PaymentLedgerStore.EventPaymentInitiated, eventType);
    }

    [Fact]
    public async Task A_conflicting_duplicate_intent_is_refused_and_leaves_no_trace()
    {
        var store = Store();
        var run = await OpenAsync(store);
        var intent = Intents(run)[0];
        Assert.True((await store.SubmitIntentAsync(TenantA, Operator, intent, "c", default)).IsSuccess);

        var changed = new PaymentIntent(
            intent.RunId, intent.IntentId, intent.Sequence, intent.EventTimeUtcMs, intent.SourceBankId, intent.SourceAccountId,
            intent.DestBankId, intent.DestAccountId, new Money(intent.Amount.Currency, intent.Amount.MinorUnits + 1), intent.Truth, intent.Features);
        var conflict = await store.SubmitIntentAsync(TenantA, Operator, changed, "c", default);
        Assert.Equal(PaymentFailureCode.ConflictingDuplicate, conflict.Error.Code);
        await AssertEffects(run, payments: 1, transitions: 1, facts: 1, commands: 1, outbox: 1);
    }

    [Fact]
    public async Task Facts_are_gap_free_in_acceptance_order_and_out_of_order_or_invalid_intents_change_nothing()
    {
        var store = Store();
        var run = await OpenAsync(store);
        var intents = Intents(run);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(i + 1, (await store.SubmitIntentAsync(TenantA, Operator, intents[i], "c", default)).Value.FactSequence);
        }

        var stale = await store.SubmitIntentAsync(TenantA, Operator, With(intents[5], sequence: intents[4].Sequence), "c", default);
        Assert.Equal(PaymentFailureCode.OutOfOrderInput, stale.Error.Code);
        var earlier = await store.SubmitIntentAsync(TenantA, Operator, With(intents[5], time: intents[4].EventTimeUtcMs - 1), "c", default);
        Assert.Equal(PaymentFailureCode.OutOfOrderInput, earlier.Error.Code);
        var unknownAccount = await store.SubmitIntentAsync(TenantA, Operator, With(intents[5], source: "BANK-A/ACC-99"), "c", default);
        Assert.Equal(PaymentFailureCode.UnknownAccount, unknownAccount.Error.Code);
        var wrongBank = await store.SubmitIntentAsync(TenantA, Operator, With(intents[5], sourceBank: "BANK-F"), "c", default);
        Assert.Equal(PaymentFailureCode.UnknownAccount, wrongBank.Error.Code);
        var eur = await store.SubmitIntentAsync(TenantA, Operator, With(intents[5], currency: "EUR"), "c", default);
        Assert.Equal(PaymentFailureCode.UnsupportedCurrency, eur.Error.Code);
        var otherRun = await store.SubmitIntentAsync(TenantA, Operator, Intents(new ScenarioRunId(Guid.NewGuid()))[5], "c", default);
        Assert.Equal(PaymentFailureCode.RunNotFound, otherRun.Error.Code);

        await AssertEffects(run, payments: 5, transitions: 5, facts: 5, commands: 5, outbox: 5);
        var sequences = await ScalarAsync<long[]>(
            "SELECT array_agg(fact_seq ORDER BY fact_seq) FROM revenant.finance_fact WHERE scenario_run_id = @r", ("r", run.Value));
        Assert.Equal([1L, 2, 3, 4, 5], sequences);
        // The next valid intent continues the order.
        Assert.Equal(6, (await store.SubmitIntentAsync(TenantA, Operator, intents[5], "c", default)).Value.FactSequence);
    }

    [Fact]
    public async Task Concurrent_identical_intakes_have_exactly_one_effect()
    {
        var store = Store();
        var run = await OpenAsync(store);
        var intent = Intents(run)[0];

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            Task.Run(() => store.SubmitIntentAsync(TenantA, Operator, intent, "c", default))));

        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal(1, results.Count(r => !r.Value.IsReplay));
        Assert.Single(results.Select(r => r.Value.Payment.PaymentId).Distinct());
        await AssertEffects(run, payments: 1, transitions: 1, facts: 1, commands: 1, outbox: 1);
    }

    [Fact]
    public async Task Concurrent_conflicting_intakes_under_one_intent_id_admit_exactly_one_version_of_the_facts()
    {
        var store = Store();
        var run = await OpenAsync(store);
        var intent = Intents(run)[0];
        var variants = Enumerable.Range(0, 12).Select(i => With(intent, amount: intent.Amount.MinorUnits + (i % 4))).ToArray();

        var results = await Task.WhenAll(variants.Select(v => Task.Run(() => store.SubmitIntentAsync(TenantA, Operator, v, "c", default))));

        Assert.Single(results, r => r.IsSuccess && !r.Value.IsReplay);
        var winner = results.Single(r => r.IsSuccess && !r.Value.IsReplay).Value.Payment.AmountMinor;
        Assert.All(results.Where(r => r.IsSuccess), r => Assert.Equal(winner, r.Value.Payment.AmountMinor));
        Assert.All(results.Where(r => r.IsFailure), r => Assert.Equal(PaymentFailureCode.ConflictingDuplicate, r.Error.Code));
        await AssertEffects(run, payments: 1, transitions: 1, facts: 1, commands: 1, outbox: 1);
    }

    [Fact]
    public async Task Concurrent_intake_of_ordered_intents_keeps_facts_gap_free_and_in_intent_order()
    {
        var store = Store();
        var run = await OpenAsync(store);
        var intents = Intents(run).Take(20).ToArray();

        var results = await Task.WhenAll(intents.Select(i => Task.Run(() => store.SubmitIntentAsync(TenantA, Operator, i, "c", default))));

        var accepted = results.Count(r => r.IsSuccess);
        Assert.True(accepted >= 1);
        Assert.All(results.Where(r => r.IsFailure), r => Assert.Equal(PaymentFailureCode.OutOfOrderInput, r.Error.Code));
        var rows = await FactSequencesWithIntentSequence(run.Value);
        Assert.Equal(accepted, rows.Count);
        Assert.Equal(Enumerable.Range(1, accepted).Select(i => (long)i), rows.Select(r => r.FactSeq));
        Assert.Equal(rows.Select(r => r.IntentSequence).Order(), rows.Select(r => r.IntentSequence));
        await AssertEffects(run, payments: accepted, transitions: accepted, facts: accepted, commands: accepted, outbox: accepted);
    }

    [Fact]
    public async Task A_failing_outbox_write_rolls_back_the_whole_intake_and_nothing_is_acknowledged()
    {
        var run = await OpenAsync(Store());
        var failing = PaymentKit.Store(db.Writer, _ => new FailingOutboxWriter());
        var intent = Intents(run)[0];

        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.SubmitIntentAsync(TenantA, Operator, intent, "c", default));

        await AssertEffects(run, payments: 0, transitions: 0, facts: 0, commands: 0, outbox: 0);
        Assert.Equal(0, await ScalarAsync<long>("SELECT last_fact_seq FROM revenant.run_cursor WHERE scenario_run_id = @r", ("r", run.Value)));
        // The same intent then succeeds normally: no half-written state blocked it.
        Assert.True((await Store().SubmitIntentAsync(TenantA, Operator, intent, "c", default)).IsSuccess);
    }

    [Fact]
    public async Task Tenant_context_is_transaction_local_so_pooled_connections_never_leak_one_tenant_into_another()
    {
        var store = Store();
        var runA = await OpenAsync(store, TenantA);
        var runB = await OpenAsync(store, TenantB, "staff:operator-b");
        var intentA = Intents(runA)[0];
        var intentB = Intents(runB)[0];
        Assert.True((await store.SubmitIntentAsync(TenantA, Operator, intentA, "c", default)).IsSuccess);
        Assert.True((await store.SubmitIntentAsync(TenantB, "staff:operator-b", intentB, "c", default)).IsSuccess);

        // A -> B -> A on the same pooled connection (pool of one connection string, sequential use).
        for (var i = 0; i < 3; i++)
        {
            Assert.NotNull(await store.GetPaymentAsync(TenantA, runA, intentA.IntentId, default));
            Assert.Null(await store.GetPaymentAsync(TenantB, runA, intentA.IntentId, default));
            Assert.NotNull(await store.GetPaymentAsync(TenantB, runB, intentB.IntentId, default));
            Assert.Null(await store.GetPaymentAsync(TenantA, runB, intentB.IntentId, default));
        }
        Assert.Null(await store.GetRunAsync(TenantB, runA, default));
        var crossTenantWrite = await store.SubmitIntentAsync(TenantB, "staff:operator-b", Intents(runA)[1], "c", default);
        Assert.Equal(PaymentFailureCode.RunNotFound, crossTenantWrite.Error.Code);

        // Directly: with no tenant context nothing is visible in any new table, with another tenant's context neither.
        foreach (var table in new[] { "run_cursor", "finance_fact", "payment_transition", "payment_command", "payment" })
        {
            await using var conn = await db.Writer.OpenConnectionAsync();
            Assert.Equal(0L, await InTenantCount(conn, null, table));
            Assert.Equal(0L, await InTenantCount(conn, "tenant-none", table));
            Assert.True(await InTenantCount(conn, TenantA, table) > 0);
            Assert.Equal(0L, await InTenantCount(conn, null, table)); // the previous transaction's setting did not stick
        }
    }

    [Fact]
    public async Task Fact_order_is_enforced_by_the_database_even_for_a_direct_writer()
    {
        var run = await OpenAsync(Store());
        var seeded = await SeedFromDatabaseAsync(run);
        await using var conn = await db.Writer.OpenConnectionAsync();
        await using var tx = await LedgerSql.BeginTenantAsync(conn, TenantA);
        var payment = await LedgerSql.InsertPaymentAsync(conn, seeded, "RAW-1", seeded.Account("BANK-A/ACC-1"), seeded.Account("BANK-B/ACC-1"), 100);
        await LedgerSql.ExecAsync(conn,
            "INSERT INTO revenant.finance_fact (tenant_id, scenario_run_id, fact_seq, kind, schema_version, payment_id, canonical, canonical_sha256) " +
            "VALUES (@t, @r, 5, 'PaymentIntent', 's', @p, '\\x01'::bytea, decode(repeat('00', 32), 'hex'))",
            ("t", TenantA), ("r", run.Value), ("p", payment));
        var ex = await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync());
        Assert.Equal("RV004", ex.SqlState);
    }

    // ------------------------------------------------------------------------------------------------------------

    private static PaymentIntent With(
        PaymentIntent i, long? sequence = null, long? time = null, string? source = null, string? sourceBank = null,
        string? currency = null, long? amount = null) =>
        new(i.RunId, i.IntentId, sequence ?? i.Sequence, time ?? i.EventTimeUtcMs, sourceBank ?? i.SourceBankId,
            source ?? i.SourceAccountId, i.DestBankId, i.DestAccountId,
            new Money(currency ?? i.Amount.Currency, amount ?? i.Amount.MinorUnits), i.Truth, i.Features);

    private async Task AssertEffects(ScenarioRunId run, int payments, int transitions, int facts, int commands, int outbox)
    {
        Assert.Equal(payments, await Count(db.Owner, "payment", run.Value));
        Assert.Equal(transitions, await Count(db.Owner, "payment_transition", run.Value));
        Assert.Equal(facts, await Count(db.Owner, "finance_fact", run.Value));
        Assert.Equal(commands, await Count(db.Owner, "payment_command", run.Value));
        Assert.Equal(outbox, await OutboxRows(db.Owner, run.Value));
    }

    private async Task<object> Snapshot(Guid run)
    {
        var counts = new List<long>();
        foreach (var table in new[] { "bank", "account", "payment", "ledger_group", "ledger_entry", "finance_fact", "payment_transition", "payment_command" })
        {
            counts.Add(await ScalarAsync<long>($"SELECT count(*) FROM revenant.{table} WHERE scenario_run_id = @r", ("r", run)));
        }
        var postings = await ScalarAsync<string>(
            "SELECT coalesce(string_agg(account_id::text || ':' || posted_minor, ',' ORDER BY account_id), '') FROM revenant.account_posted_balance WHERE scenario_run_id = @r",
            ("r", run));
        return new { Counts = string.Join(",", counts), Postings = postings };
    }

    private async Task<List<(long FactSeq, long IntentSequence)>> FactSequencesWithIntentSequence(Guid run)
    {
        var rows = new List<(long, long)>();
        await using var cmd = db.Owner.CreateCommand("SELECT fact_seq, canonical FROM revenant.finance_fact WHERE scenario_run_id = @r ORDER BY fact_seq");
        cmd.Parameters.AddWithValue("r", run);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetInt64(0), PaymentIntentCodec.Instance.Decode(reader.GetFieldValue<byte[]>(1)).Sequence));
        }
        return rows;
    }

    private async Task<SeededRun> SeedFromDatabaseAsync(ScenarioRunId run)
    {
        var banks = new Dictionary<string, Guid>();
        var accounts = new Dictionary<string, Guid>();
        var opening = new Dictionary<string, long>();
        await using (var cmd = db.Owner.CreateCommand("SELECT local_bank_id, id FROM revenant.bank WHERE scenario_run_id = @r"))
        {
            cmd.Parameters.AddWithValue("r", run.Value);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                banks[reader.GetString(0)] = reader.GetGuid(1);
            }
        }
        await using (var cmd = db.Owner.CreateCommand(
            "SELECT local_account_id, id, opening_balance_minor FROM revenant.account WHERE scenario_run_id = @r"))
        {
            cmd.Parameters.AddWithValue("r", run.Value);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                accounts[reader.GetString(0)] = reader.GetGuid(1);
                opening[reader.GetString(0)] = reader.GetInt64(2);
            }
        }
        return new SeededRun(TenantA, run.Value, banks, accounts, opening);
    }

    private static async Task<long> InTenantCount(NpgsqlConnection conn, string? tenant, string table)
    {
        await using var tx = await LedgerSql.BeginTenantAsync(conn, tenant);
        var count = await LedgerSql.ScalarAsync<long>(conn, $"SELECT count(*) FROM revenant.{table}");
        await tx.CommitAsync();
        return count;
    }

    private Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] args) => PaymentKit.Scalar<T>(db.Owner, sql, args);

    private async Task DeniedForWriterAsync(string sql, string sqlState)
    {
        await using var conn = await db.Writer.OpenConnectionAsync();
        await using var tx = await LedgerSql.BeginTenantAsync(conn, TenantA);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => LedgerSql.ExecAsync(conn, sql));
        Assert.Equal(sqlState, ex.SqlState);
    }
}
