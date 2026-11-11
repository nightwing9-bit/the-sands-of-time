using FsCheck;
using FsCheck.Fluent;
using Npgsql;
using Revenant.Finance;
using Revenant.Payments;
using Revenant.Tests.Ledger;
using LedgerSql = Revenant.Tests.Ledger.Ledger;
using Xunit;
using Xunit.Abstractions;
using static Revenant.Tests.Payments.PaymentKit;

namespace Revenant.Tests.Payments;

/// <summary>
/// Spec 07 task 7.2, payment machine side (R07.02, R07.03, R07.09): the full legal matrix, one legal state and one effect per
/// command, idempotent and conflicting repeats. The property oracle is an independent hand-written table of the design's
/// frozen lifecycle, not the code under test.
/// </summary>
[Trait("Category", "Integration")]
[Collection(PaymentCollection.Name)]
public sealed class PaymentMachineTests(LedgerDatabase db, ITestOutputHelper output)
{
    // The design's frozen matrix, written out independently of PaymentLifecycle.
    private static readonly HashSet<(PaymentStatus, PaymentStatus)> Oracle =
    [
        (PaymentStatus.Initiated, PaymentStatus.Screened),
        (PaymentStatus.Screened, PaymentStatus.Held), (PaymentStatus.Screened, PaymentStatus.Cleared), (PaymentStatus.Screened, PaymentStatus.Rejected),
        (PaymentStatus.Held, PaymentStatus.Approved), (PaymentStatus.Held, PaymentStatus.Rejected), (PaymentStatus.Held, PaymentStatus.Returned),
        (PaymentStatus.Approved, PaymentStatus.Cleared), (PaymentStatus.Approved, PaymentStatus.Returned),
        (PaymentStatus.Cleared, PaymentStatus.Settled), (PaymentStatus.Cleared, PaymentStatus.Returned),
        (PaymentStatus.Settled, PaymentStatus.Returned),
    ];

    private static readonly PaymentStatus[] All = Enum.GetValues<PaymentStatus>();

    private PaymentLedgerStore Store() => PaymentKit.Store(db.Writer);

    [Fact]
    public void The_pure_matrix_equals_the_design_and_the_machine_decides_one_step_at_most()
    {
        foreach (var from in All)
        {
            foreach (var to in All)
            {
                Assert.Equal(Oracle.Contains((from, to)), PaymentLifecycle.IsLegal(from, to));
                var decision = PaymentMachine.Decide(from, 3, to, 3);
                var expected = !Oracle.Contains((from, to)) ? PaymentDecisionKind.Illegal
                    : from == PaymentStatus.Settled || to == PaymentStatus.Settled ? PaymentDecisionKind.PostingRequired
                    : PaymentDecisionKind.Accepted;
                Assert.Equal(expected, decision.Kind);
                Assert.Equal(expected == PaymentDecisionKind.Accepted ? 4 : 3, decision.NextVersion);
                Assert.Equal(PaymentDecisionKind.Stale, PaymentMachine.Decide(from, 3, to, 2).Kind);
            }
        }
        Assert.All(All.Where(PaymentLifecycle.IsTerminal), s => Assert.DoesNotContain(Oracle, e => e.Item1 == s));
        Assert.Equal([PaymentStatus.Rejected, PaymentStatus.Returned], All.Where(PaymentLifecycle.IsTerminal).Order());
    }

    [Fact]
    public async Task The_database_matrix_equals_the_csharp_matrix_for_all_64_pairs()
    {
        foreach (var from in All)
        {
            foreach (var to in All)
            {
                var legalInDb = await ScalarAsync<bool>(
                    "SELECT revenant.is_legal_payment_transition(@f, @t)", ("f", from.ToString()), ("t", to.ToString()));
                Assert.True(PaymentLifecycle.IsLegal(from, to) == legalInDb, $"{from} -> {to}");
            }
        }
    }

    [Fact]
    public async Task A_legal_command_moves_one_step_and_a_repeat_replays_without_a_second_effect()
    {
        var store = Store();
        var (run, intent) = await NewPaymentAsync(store);

        var screened = await store.TransitionAsync(TenantA, Operator, Cmd(run, intent, PaymentStatus.Screened, 1, "screen-1"), "c", default);
        Assert.False(screened.Value.IsReplay);
        Assert.Equal((PaymentStatus.Screened, 2L), (screened.Value.Payment.Status, screened.Value.Payment.Version));
        Assert.Equal(PaymentLedgerStore.EventPaymentTransitioned, await ScalarAsync<string>(
            "SELECT event_type FROM revenant.outbox WHERE event_id = (SELECT o.event_id FROM revenant.outbox o JOIN revenant.payment p " +
            "ON o.aggregate_id = 'payment:' || replace(p.id::text, '-', '') WHERE p.scenario_run_id = @r ORDER BY o.seq DESC LIMIT 1)", ("r", run.Value)));

        // The same command id and request again, even after the payment moved on: replay of the first result.
        var held = await store.TransitionAsync(TenantA, Operator, Cmd(run, intent, PaymentStatus.Held, 2, "hold-1", "velocity"), "c", default);
        Assert.Equal(3, held.Value.Payment.Version);
        var again = await store.TransitionAsync(TenantA, Operator, Cmd(run, intent, PaymentStatus.Screened, 1, "screen-1"), "c", default);
        Assert.True(again.Value.IsReplay);
        Assert.Equal((PaymentStatus.Screened, 2L), (again.Value.AppliedStatus, again.Value.AppliedVersion));
        Assert.Equal(PaymentStatus.Held, again.Value.Payment.Status);

        Assert.Equal(3, await Count(db.Owner, "payment_transition", run.Value));
        Assert.Equal(3, await OutboxRows(db.Owner, run.Value));
        Assert.Equal(3, await Count(db.Owner, "payment_command", run.Value));
        var trail = (await store.GetPaymentAsync(TenantA, run, intent.IntentId, default))!.Trail;
        Assert.Equal([PaymentStatus.Initiated, PaymentStatus.Screened, PaymentStatus.Held], trail.Select(t => t.To));
        Assert.Equal("velocity", trail[2].Reason);
        Assert.True(PaymentLifecycle.IsLegalTrail([.. trail.Select(t => t.To)]));
    }

    [Fact]
    public async Task Illegal_and_money_moving_steps_are_recorded_as_rejected_results_with_no_trail_row_and_no_event()
    {
        var store = Store();
        var (run, intent) = await NewPaymentAsync(store);

        var illegal = await store.TransitionAsync(TenantA, Operator, Cmd(run, intent, PaymentStatus.Cleared, 1, "bad-1"), "c", default);
        Assert.Equal(PaymentFailureCode.IllegalTransition, illegal.Error.Code);
        var repeated = await store.TransitionAsync(TenantA, Operator, Cmd(run, intent, PaymentStatus.Cleared, 1, "bad-1"), "c", default);
        Assert.Equal(PaymentFailureCode.IllegalTransition, repeated.Error.Code);

        foreach (var (target, id) in new[] { (PaymentStatus.Screened, "s"), (PaymentStatus.Cleared, "c") })
        {
            Assert.True((await store.TransitionAsync(TenantA, Operator,
                Cmd(run, intent, target, (await Version(run, intent))!.Value, "ok-" + id), "c", default)).IsSuccess);
        }
        var settle = await store.TransitionAsync(TenantA, Operator, Cmd(run, intent, PaymentStatus.Settled, 3, "settle-1"), "c", default);
        Assert.Equal(PaymentFailureCode.PostingRequired, settle.Error.Code);

        Assert.Equal(3, await Count(db.Owner, "payment_transition", run.Value)); // Initiated, Screened, Cleared
        Assert.Equal(3, await OutboxRows(db.Owner, run.Value));
        Assert.Equal(2L, await ScalarAsync<long>(
            "SELECT count(*) FROM revenant.payment_command WHERE scenario_run_id = @r AND outcome = 'Rejected'", ("r", run.Value)));
        Assert.Equal(PaymentStatus.Cleared, (await store.GetPaymentAsync(TenantA, run, intent.IntentId, default))!.Status);
    }

    [Fact]
    public async Task Stale_versions_unknown_payments_and_command_id_reuse_have_typed_failures_and_no_effect()
    {
        var store = Store();
        var (run, intent) = await NewPaymentAsync(store);

        var stale = await store.TransitionAsync(TenantA, Operator, Cmd(run, intent, PaymentStatus.Screened, 7, "stale-1"), "c", default);
        Assert.Equal(PaymentFailureCode.StaleVersion, stale.Error.Code);
        Assert.Equal(1, await Count(db.Owner, "payment_command", run.Value)); // stale is not recorded

        Assert.True((await store.TransitionAsync(TenantA, Operator, Cmd(run, intent, PaymentStatus.Screened, 1, "k-1"), "c", default)).IsSuccess);
        var reused = await store.TransitionAsync(TenantA, Operator, Cmd(run, intent, PaymentStatus.Held, 2, "k-1"), "c", default);
        Assert.Equal(PaymentFailureCode.ConflictingDuplicate, reused.Error.Code);

        var missing = await store.TransitionAsync(TenantA, Operator, Cmd(run, intent, PaymentStatus.Screened, 1, "k-2", intentId: "NOPE"), "c", default);
        Assert.Equal(PaymentFailureCode.PaymentNotFound, missing.Error.Code);
        var malformed = await store.TransitionAsync(TenantA, Operator, Cmd(run, intent, PaymentStatus.Screened, 0, "k-3"), "c", default);
        Assert.Equal(PaymentFailureCode.InvalidRequest, malformed.Error.Code);
        var badId = await store.TransitionAsync(TenantA, Operator, Cmd(run, intent, PaymentStatus.Screened, 1, "has space"), "c", default);
        Assert.Equal(PaymentFailureCode.InvalidRequest, badId.Error.Code);
        var otherTenant = await store.TransitionAsync(TenantB, Operator, Cmd(run, intent, PaymentStatus.Screened, 2, "k-4"), "c", default);
        Assert.Equal(PaymentFailureCode.PaymentNotFound, otherTenant.Error.Code);

        Assert.Equal(2, await Count(db.Owner, "payment_transition", run.Value));
        Assert.Equal(2, await Count(db.Owner, "payment_command", run.Value));
    }

    [Fact]
    public async Task Concurrent_commands_on_one_payment_produce_exactly_one_state_step()
    {
        var store = Store();
        var (run, intent) = await NewPaymentAsync(store);

        // 8 different commands race for the same expected version: one wins, the rest are stale.
        var racing = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            Task.Run(() => store.TransitionAsync(TenantA, Operator, Cmd(run, intent, PaymentStatus.Screened, 1, $"race-{i}"), "c", default))));
        Assert.Single(racing, r => r.IsSuccess);
        Assert.All(racing.Where(r => r.IsFailure), r => Assert.Equal(PaymentFailureCode.StaleVersion, r.Error.Code));
        Assert.Equal(2L, await Version(run, intent));

        // 8 identical commands (one id): all succeed, only one is a first execution.
        var same = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => store.TransitionAsync(TenantA, Operator, Cmd(run, intent, PaymentStatus.Held, 2, "same-1"), "c", default))));
        Assert.All(same, r => Assert.True(r.IsSuccess));
        Assert.Equal(1, same.Count(r => !r.Value.IsReplay));
        Assert.Equal(3L, await Version(run, intent));
        Assert.Equal(3, await Count(db.Owner, "payment_transition", run.Value));
        Assert.Equal(3, await OutboxRows(db.Owner, run.Value));
    }

    [Fact]
    public async Task The_trail_command_and_fact_tables_are_append_only_for_the_writer_and_the_owner_and_the_replay_reader_cannot_write()
    {
        var store = Store();
        var (run, intent) = await NewPaymentAsync(store);
        Assert.True((await store.TransitionAsync(TenantA, Operator, Cmd(run, intent, PaymentStatus.Screened, 1, "a-1"), "c", default)).IsSuccess);

        foreach (var table in new[] { "payment_transition", "payment_command", "finance_fact", "scenario_run", "scenario_run_staff" })
        {
            await Denied(db.Writer, TenantA, $"UPDATE revenant.{table} SET {(table == "scenario_run" ? "id" : "scenario_run_id")} = {(table == "scenario_run" ? "id" : "scenario_run_id")}", "42501");
            await Denied(db.Writer, TenantA, $"DELETE FROM revenant.{table}", "42501");
            await Denied(db.Writer, TenantA, $"TRUNCATE revenant.{table}", "42501");
            await Denied(db.Owner, TenantA, $"UPDATE revenant.{table} SET {(table == "scenario_run" ? "id" : "scenario_run_id")} = {(table == "scenario_run" ? "id" : "scenario_run_id")}", "RV001");
            await Denied(db.Owner, TenantA, $"DELETE FROM revenant.{table}", "RV001");
            // PostgreSQL itself refuses to truncate a table that a foreign key references (scenario_run; since task 7.4 also
            // scenario_run_staff, referenced by scenario_run_staff_revocation) before any trigger runs.
            await Denied(db.Owner, TenantA, $"TRUNCATE revenant.{table}", table is "scenario_run" or "scenario_run_staff" ? "0A000" : "RV001");
        }
        // The cursor only advances by one fact; a payment status cannot skip the matrix or the version.
        await Denied(db.Writer, TenantA, "UPDATE revenant.run_cursor SET last_fact_seq = last_fact_seq + 2", "RV004");
        await Denied(db.Writer, TenantA, "UPDATE revenant.run_cursor SET last_fact_seq = last_fact_seq", "RV004");
        await Denied(db.Writer, TenantA, "UPDATE revenant.payment SET status = 'Settled', version = version + 1", "RV003");
        await Denied(db.Writer, TenantA, "UPDATE revenant.payment SET status = 'Held'", "RV003");
        await Denied(db.Writer, TenantA, "UPDATE revenant.payment SET status = 'Held', version = version + 5", "RV003");
        await Denied(db.Writer, TenantA,
            "INSERT INTO revenant.payment_transition (tenant_id, scenario_run_id, payment_id, version, from_status, to_status, command_key, actor) " +
            "SELECT tenant_id, scenario_run_id, id, 9, 'Initiated', 'Settled', 'x', 'y' FROM revenant.payment", "23514");

        foreach (var table in new[] { "payment_transition", "payment_command", "finance_fact", "run_cursor", "scenario_run", "payment" })
        {
            await Denied(db.Replay, TenantA, $"DELETE FROM revenant.{table}", "42501");
        }
        await Denied(db.Replay, TenantA, "UPDATE revenant.payment SET status = 'Held'", "42501");
        await Denied(db.Replay, TenantA, "INSERT INTO revenant.finance_fact (tenant_id) VALUES ('x')", "42501");
        await Denied(db.Replay, TenantA, "SELECT 1 FROM revenant.outbox", "42501");
        Assert.True(await ScalarAsync<long>("SELECT count(*) FROM revenant.payment_transition WHERE scenario_run_id = @r", ("r", run.Value)) == 2);
    }

    /// <summary>
    /// Property (R07.02): for random command sequences (legal, illegal, stale, money-moving, repeated) against a real payment
    /// in PostgreSQL, every command leaves exactly one legal state: the stored status/version equal an independent model,
    /// the trail is a legal path whose length equals the version, an accepted command adds exactly one trail row and one
    /// outbox event, and a rejected, stale or repeated command adds none.
    /// </summary>
    [Fact]
    public void Random_command_sequences_leave_one_legal_state_and_one_effect_per_command()
    {
        var step =
            from preferLegal in Gen.Frequency((7, Gen.Constant(true)), (3, Gen.Constant(false)))
            from pick in Gen.Choose(0, 1000)
            from stale in Gen.Frequency((8, Gen.Constant(false)), (2, Gen.Constant(true)))
            from repeat in Gen.Frequency((7, Gen.Constant(false)), (3, Gen.Constant(true)))
            select (PreferLegal: preferLegal, Pick: pick, Stale: stale, Repeat: repeat);
        var sequences = from n in Gen.Choose(1, 14) from list in Gen.ArrayOf(step, n) select list;
        var observed = new Dictionary<string, int>();

        Prop.ForAll(sequences.ToArbitrary(), commands =>
        {
            var store = Store();
            var (run, intent) = NewPaymentAsync(store).GetAwaiter().GetResult();
            var status = PaymentStatus.Initiated;
            long version = 1;
            var trail = new List<PaymentStatus> { PaymentStatus.Initiated };
            long events = 1;
            var n = 0;
            foreach (var (preferLegal, pick, stale, repeat) in commands)
            {
                var legalNext = All.Where(t => Oracle.Contains((status, t))).ToArray();
                var target = preferLegal && legalNext.Length > 0 ? legalNext[pick % legalNext.Length] : All[pick % All.Length];
                var expectedVersion = stale ? version + 1 : version;
                var id = $"cmd-{n++}";
                var command = Cmd(run, intent, target, expectedVersion, id);

                var result = store.TransitionAsync(TenantA, Operator, command, "c", default).GetAwaiter().GetResult();
                string kind;
                if (stale)
                {
                    kind = "stale";
                    if (result.IsSuccess || result.Error.Code != PaymentFailureCode.StaleVersion) { return false; }
                }
                else if (!Oracle.Contains((status, target)))
                {
                    kind = "illegal";
                    if (result.IsSuccess || result.Error.Code != PaymentFailureCode.IllegalTransition) { return false; }
                }
                else if (status == PaymentStatus.Settled || target == PaymentStatus.Settled)
                {
                    kind = "posting";
                    if (result.IsSuccess || result.Error.Code != PaymentFailureCode.PostingRequired) { return false; }
                }
                else if (status == PaymentStatus.Held && target == PaymentStatus.Approved)
                {
                    // Task 7.4: releasing a held payment belongs to its review case, never to a generic transition.
                    kind = "review";
                    if (result.IsSuccess || result.Error.Code != PaymentFailureCode.ReviewRequired) { return false; }
                }
                else
                {
                    kind = "accepted";
                    if (result.IsFailure || result.Value.IsReplay || result.Value.AppliedVersion != version + 1) { return false; }
                    (status, version) = (target, version + 1);
                    trail.Add(target);
                    events++;
                }
                observed[kind] = observed.GetValueOrDefault(kind) + 1;

                if (repeat)
                {
                    var again = store.TransitionAsync(TenantA, Operator, command, "c", default).GetAwaiter().GetResult();
                    var same = kind switch
                    {
                        "accepted" => again.IsSuccess && again.Value.IsReplay && again.Value.AppliedVersion == version,
                        "illegal" => again.IsFailure && again.Error.Code == PaymentFailureCode.IllegalTransition,
                        "posting" => again.IsFailure && again.Error.Code == PaymentFailureCode.PostingRequired,
                        "review" => again.IsFailure && again.Error.Code == PaymentFailureCode.ReviewRequired,
                        _ => again.IsFailure && again.Error.Code == PaymentFailureCode.StaleVersion,
                    };
                    if (!same) { return false; }
                    observed["repeat"] = observed.GetValueOrDefault("repeat") + 1;
                }

                var stored = store.GetPaymentAsync(TenantA, run, intent.IntentId, default).GetAwaiter().GetResult()!;
                var eventCount = OutboxRows(db.Owner, run.Value).GetAwaiter().GetResult();
                if (stored.Status != status || stored.Version != version || stored.Trail.Count != version || eventCount != events
                    || !stored.Trail.Select(t => t.To).SequenceEqual(trail) || !PaymentLifecycle.IsLegalTrail([.. trail])
                    || stored.Trail.Select(t => t.Version).SequenceEqual(Enumerable.Range(1, (int)version).Select(v => (long)v)) is false)
                {
                    return false;
                }
            }
            return true;
        }).Check(Config.QuickThrowOnFailure.WithMaxTest(30));

        output.WriteLine("command outcomes observed across the generated sequences: " + string.Join(", ", observed.OrderBy(o => o.Key).Select(o => $"{o.Key}={o.Value}")));
        Assert.True(observed.GetValueOrDefault("accepted") > 0 && observed.GetValueOrDefault("illegal") > 0
            && observed.GetValueOrDefault("stale") > 0 && observed.GetValueOrDefault("repeat") > 0,
            "the generator must exercise accepted, illegal, stale and repeated commands");
    }

    // ------------------------------------------------------------------------------------------------------------

    private static async Task<(Revenant.Contracts.ScenarioRunId Run, Revenant.Contracts.PaymentIntent Intent)> NewPaymentAsync(PaymentLedgerStore store)
    {
        var run = await OpenAsync(store);
        var intent = Intents(run)[0];
        Assert.True((await store.SubmitIntentAsync(TenantA, Operator, intent, "c", default)).IsSuccess);
        return (run, intent);
    }

    private static TransitionCommand Cmd(
        Revenant.Contracts.ScenarioRunId run, Revenant.Contracts.PaymentIntent intent, PaymentStatus target, long expected, string id,
        string? reason = null, string? intentId = null) =>
        new(run, intentId ?? intent.IntentId, target, expected, id, reason);

    private async Task<long?> Version(Revenant.Contracts.ScenarioRunId run, Revenant.Contracts.PaymentIntent intent) =>
        await ScalarAsync<long?>("SELECT version FROM revenant.payment WHERE scenario_run_id = @r AND intent_id = @i",
            ("r", run.Value), ("i", intent.IntentId));

    private Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] args) => PaymentKit.Scalar<T>(db.Owner, sql, args);

    private static async Task Denied(NpgsqlDataSource source, string tenant, string sql, string sqlState)
    {
        await using var conn = await source.OpenConnectionAsync();
        await using var tx = await LedgerSql.BeginTenantAsync(conn, tenant);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => LedgerSql.ExecAsync(conn, sql));
        Assert.True(sqlState == ex.SqlState, $"{sql}: expected {sqlState}, got {ex.SqlState} ({ex.MessageText})");
    }
}
