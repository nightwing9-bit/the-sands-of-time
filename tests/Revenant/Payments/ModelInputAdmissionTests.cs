using System.Security.Cryptography;
using Marrow.Infrastructure.Messaging;
using Revenant.Contracts;
using Revenant.Intelligence;
using Revenant.Payments;
using Revenant.Scenario;
using Revenant.Tests.Ledger;
using Xunit;
using LedgerSql = Revenant.Tests.Ledger.Ledger;

namespace Revenant.Tests.Payments;

[Collection(PaymentCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ModelInputAdmissionTests(LedgerDatabase db)
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string Owner = PaymentKit.Operator;

    [Fact]
    public async Task V3_checks_the_inclusive_amount_bound_before_effects_and_keeps_intake_idempotent()
    {
        var store = Store();
        var tenant = Guid.NewGuid().ToString("N");
        var v3 = await OpenAsync(store, tenant);
        var v2 = await OpenAsync(store, tenant, PaymentLedgerStore.FinanceVersion);
        var intents = PaymentKit.Intents(v3);
        var oversized = WithAmount(intents[0], RiskFeaturesV1.MaxMinorUnits + 1);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var rejected = await store.SubmitIntentAsync(tenant, Owner, oversized, "oversized", Ct);
            Assert.Equal(PaymentFailureCode.InvalidRequest, rejected.Error.Code);
            foreach (var table in new[] { "payment", "payment_transition", "payment_command", "finance_fact", "audit_event" })
            {
                Assert.Equal(0, await PaymentKit.Count(db.Owner, table, v3.Value));
            }
            Assert.Equal(0, await OutboxCountAsync(v3));
            Assert.True(await PaymentKit.Scalar<bool>(db.Owner, """
                SELECT last_fact_seq = 0 AND last_intent_sequence = 0 AND last_event_time_ms = 0
                  FROM revenant.run_cursor WHERE scenario_run_id = @r
                """, ("r", v3.Value)));
        }

        var boundary = WithAmount(intents[0], RiskFeaturesV1.MaxMinorUnits);
        var admitted = (await store.SubmitIntentAsync(tenant, Owner, boundary, "boundary", Ct)).Value;
        Assert.Equal(1, admitted.FactSequence);
        var retry = (await store.SubmitIntentAsync(tenant, Owner, boundary, "retry", Ct)).Value;
        Assert.True(retry.IsReplay);
        Assert.Equal(admitted.Payment.PaymentId, retry.Payment.PaymentId);
        Assert.Equal(PaymentFailureCode.ConflictingDuplicate, (await store.SubmitIntentAsync(tenant, Owner, oversized, "conflict", Ct)).Error.Code);
        Assert.Equal(2, (await store.SubmitIntentAsync(tenant, Owner, intents[1], "valid", Ct)).Value.FactSequence);
        var v2Intent = WithAmount(PaymentKit.Intents(v2)[0], RiskFeaturesV1.MaxMinorUnits + 1);
        Assert.True((await store.SubmitIntentAsync(tenant, Owner, v2Intent, "v2", Ct)).IsSuccess);
        var outcomes = await store.ScreenBatchAsync(tenant, 10, "screen", Ct);
        Assert.Equal(3, outcomes.Count);
        Assert.Contains(outcomes, row => row.RunId == v3 && row.IntentId == intents[1].IntentId && row.Evidence is not null);
        Assert.Contains(outcomes, row => row.RunId == v2 && row.Evidence is null);

        var manifest = FridayRingScenario.Create(42, ScenarioSchemas.FridayRingVersion, v3).Value.Manifest;
        Assert.Equal(66_000_000L, manifest.Accounts.Sum(a => a.OpeningBalance.MinorUnits));
    }

    [Fact]
    public async Task Incompatible_queue_heads_leave_no_effects_and_do_not_starve_other_runs()
    {
        var store = Store();
        var tenant = Guid.NewGuid().ToString("N");
        var amountRun = await OpenAsync(store, tenant, id: OrderedId(1));
        var balanceRun = await OpenAsync(store, tenant, id: OrderedId(2));
        var v2 = await OpenAsync(store, tenant, PaymentLedgerStore.FinanceVersion, OrderedId(3));
        var v3 = await OpenAsync(store, tenant, id: OrderedId(4));
        var legacy = WithAmount(PaymentKit.Intents(amountRun)[0], RiskFeaturesV1.MaxMinorUnits + 1);
        await SeedLegacyIntakeAsync(tenant, legacy);
        var retry = (await store.SubmitIntentAsync(tenant, Owner, legacy, "retry", Ct)).Value;
        Assert.True(retry.IsReplay);
        Assert.Equal(1, retry.FactSequence);
        Assert.True((await store.SubmitIntentAsync(tenant, Owner, PaymentKit.Intents(amountRun)[1], "queued", Ct)).IsSuccess);

        var original = PaymentKit.Intents(balanceRun)[0];
        const string source = "test-large-opening-account";
        await using (var conn = await db.Writer.OpenConnectionAsync())
        await using (var tx = await LedgerSql.BeginTenantAsync(conn, tenant))
        {
            Assert.Equal(1, await LedgerSql.ExecAsync(conn, """
                INSERT INTO revenant.account (tenant_id, scenario_run_id, bank_id, local_account_id, currency, opening_balance_minor)
                SELECT tenant_id, scenario_run_id, id, @source, 'USD', @opening FROM revenant.bank
                 WHERE scenario_run_id = @run AND local_bank_id = @bank
                """, ("run", balanceRun.Value), ("source", source), ("bank", original.SourceBankId),
                ("opening", RiskFeaturesV1.MaxMinorUnits + 1)));
            await tx.CommitAsync();
        }
        var highBalance = new PaymentIntent(balanceRun, original.IntentId, original.Sequence, original.EventTimeUtcMs,
            original.SourceBankId, source, original.DestBankId, original.DestAccountId, original.Amount, original.Truth, original.Features);
        var balanceRejection = (await store.SubmitIntentAsync(tenant, Owner, highBalance, "balance", Ct)).Error;
        Assert.Equal(PaymentFailureCode.InvalidRequest, balanceRejection.Code);
        Assert.Contains(nameof(RiskFeatureError.SourcePostedBalanceInvalid), balanceRejection.Message);
        Assert.All(await CountsAsync(balanceRun), count => Assert.Equal(0, count));
        await SeedLegacyIntakeAsync(tenant, highBalance);
        foreach (var run in new[] { v2, v3 })
        {
            Assert.True((await store.SubmitIntentAsync(tenant, Owner, PaymentKit.Intents(run)[0], "valid", Ct)).IsSuccess);
        }

        var before = new[] { await CountsAsync(amountRun), await CountsAsync(balanceRun) };
        var blocked = new List<BlockedScreening>();
        var results = await store.ScreenBatchAsync(tenant, 2, "screen", Ct, blocked.Add);
        Assert.Equal([v2, v3], results.Select(r => r.RunId));
        Assert.Null(results[0].Evidence);
        Assert.NotNull(results[1].Evidence);
        Assert.Equal([(amountRun, RiskFeatureError.AmountOutOfRange), (balanceRun, RiskFeatureError.SourcePostedBalanceInvalid)],
            blocked.Select(b => (b.RunId, b.FeatureError!.Value)));
        Assert.All(blocked, b => Assert.Equal(RiskModelError.FeaturesIncompatible, b.Failure.Code));
        Assert.Equal(before[0], await CountsAsync(amountRun));
        Assert.Equal(before[1], await CountsAsync(balanceRun));
        Assert.Equal(2L, await InitiatedCountAsync(amountRun));
        Assert.Equal(1L, await InitiatedCountAsync(balanceRun));
        Assert.Equal(PaymentFailureCode.RunNotQuiescent, (await store.CloseRunAsync(tenant, Owner, amountRun, "close", Ct)).Error.Code);

        // A competing Worker may bypass the locked run, but it must never claim its second payment.
        await using var competing = await db.Writer.OpenConnectionAsync();
        await using var heldHead = await LedgerSql.BeginTenantAsync(competing, tenant);
        await LedgerSql.ScalarAsync<Guid>(competing,
            "SELECT id FROM revenant.payment WHERE scenario_run_id = @run AND intent_id = @intent FOR UPDATE",
            ("run", amountRun.Value), ("intent", legacy.IntentId));
        blocked.Clear();
        Assert.Empty(await store.ScreenBatchAsync(tenant, 1, "competing", Ct, blocked.Add));
        Assert.Equal(balanceRun, Assert.Single(blocked).RunId);
        Assert.Equal(before[0], await CountsAsync(amountRun));
    }

    private PaymentLedgerStore Store() => new(db.Writer,
        tx => new PostgresOutboxWriter(tx, new MessagingTables("revenant")), TimeProvider.System,
        new RiskModelProvider(RiskModelOptions.DefaultArtifactRoot));

    private static ScenarioRunId OrderedId(int order) =>
        new(Guid.Parse($"{order:x8}{Guid.NewGuid().ToString("D")[8..]}"));

    private static async Task<ScenarioRunId> OpenAsync(PaymentLedgerStore store, string tenant,
        string version = PaymentLedgerStore.ModelAwareFinanceVersion, ScenarioRunId? id = null)
    {
        var run = id ?? new ScenarioRunId(Guid.NewGuid());
        Assert.True((await store.OpenRunAsync(tenant, Owner, "operator", 42, run, Ct, version)).IsSuccess);
        return run;
    }

    private static PaymentIntent WithAmount(PaymentIntent intent, long minor) =>
        new(intent.RunId, intent.IntentId, intent.Sequence, intent.EventTimeUtcMs, intent.SourceBankId, intent.SourceAccountId,
            intent.DestBankId, intent.DestAccountId, Money.Usd(minor), intent.Truth, intent.Features);

    private Task<long> OutboxCountAsync(ScenarioRunId run) => PaymentKit.Scalar<long>(db.Owner,
        "SELECT count(*) FROM revenant.outbox WHERE payload::jsonb ->> 'runId' = @r", ("r", run.Value.ToString()));

    private Task<long> InitiatedCountAsync(ScenarioRunId run) => PaymentKit.Scalar<long>(db.Owner,
        "SELECT count(*) FROM revenant.payment WHERE scenario_run_id = @r AND status = 'Initiated'", ("r", run.Value));

    private async Task<long[]> CountsAsync(ScenarioRunId run)
    {
        var counts = new List<long>();
        foreach (var table in new[] { "payment", "payment_transition", "payment_command", "finance_fact", "audit_event", "hold", "review_case", "ledger_entry" })
        {
            counts.Add(await PaymentKit.Count(db.Owner, table, run.Value));
        }
        counts.Add(await OutboxCountAsync(run));
        return [.. counts];
    }

    private async Task SeedLegacyIntakeAsync(string tenant, PaymentIntent intent)
    {
        // Insert the pre-guard intake shape into the disposable fixture with the ordinary writer role.
        var canonical = PaymentIntentCodec.Instance.EncodeCanonical(intent);
        await using var conn = await db.Writer.OpenConnectionAsync();
        await using var tx = await LedgerSql.BeginTenantAsync(conn, tenant);
        var payment = await LedgerSql.ScalarAsync<Guid>(conn, """
            INSERT INTO revenant.payment (tenant_id, scenario_run_id, intent_id, source_account_id, dest_account_id, currency, amount_minor, status)
            SELECT @tenant, @run, @intent, s.id, d.id, 'USD', @amount, 'Initiated'
              FROM revenant.account s JOIN revenant.account d USING (scenario_run_id)
             WHERE s.scenario_run_id = @run AND s.local_account_id = @source AND d.local_account_id = @dest RETURNING id
            """, ("tenant", tenant), ("run", intent.RunId.Value), ("intent", intent.IntentId), ("amount", intent.Amount.MinorUnits),
            ("source", intent.SourceAccountId), ("dest", intent.DestAccountId));
        await LedgerSql.ExecAsync(conn, """
            INSERT INTO revenant.payment_transition (tenant_id, scenario_run_id, payment_id, version, to_status, command_key, actor)
            VALUES (@tenant, @run, @payment, 1, 'Initiated', @key, @actor);
            UPDATE revenant.run_cursor SET last_fact_seq = 1, last_intent_sequence = @sequence, last_event_time_ms = @time
             WHERE scenario_run_id = @run;
            INSERT INTO revenant.finance_fact (tenant_id, scenario_run_id, fact_seq, kind, schema_version, payment_id, canonical, canonical_sha256)
            VALUES (@tenant, @run, 1, 'PaymentIntent', @schema, @payment, @canonical, @hash);
            INSERT INTO revenant.payment_command
                (tenant_id, scenario_run_id, command_key, kind, payment_id, request_sha256, outcome, result_status, result_version, actor)
            VALUES (@tenant, @run, @key, 'Intake', @payment, @hash, 'Accepted', 'Initiated', 1, @actor);
            """, ("tenant", tenant), ("run", intent.RunId.Value), ("payment", payment), ("key", $"intake:{intent.IntentId}"),
            ("actor", Owner), ("sequence", intent.Sequence), ("time", intent.EventTimeUtcMs),
            ("schema", ScenarioSchemas.InputSchemaVersion), ("canonical", canonical), ("hash", SHA256.HashData(canonical)));
        await tx.CommitAsync();
    }
}
