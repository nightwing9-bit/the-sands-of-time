using Npgsql;
using Revenant.Cases;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Payments;
using Revenant.Scenario;
using Revenant.Tests.Ledger;
using Xunit;
using Xunit.Abstractions;

namespace Revenant.Tests.Payments;

/// <summary>
/// Spec 07 exit review: run closure. Closing serializes with concurrent review actions on the run cursor; an action that commits
/// first is part of the run, one that arrives later is refused by PostgreSQL (RV009) with no fact, step, posting or event, and
/// the closing point always equals the final committed fact. Real PostgreSQL 18 through the least-privilege writer login.
/// **Validates: Requirements R07.12 (run identity and ordered facts), R07.09 (outbox with the same transaction)**
/// </summary>
[Collection(PaymentCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RunClosureTests(LedgerDatabase db, ITestOutputHelper output)
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string Actor = "staff:operator-1";

    private async Task<(PaymentLedgerStore Store, string Tenant, ScenarioRunId Run, List<string> Held)> HeldRunAsync(int holds)
    {
        var tenant = $"tcl-{Guid.NewGuid():N}"[..14];
        var store = PaymentKit.Store(db.Writer);
        var run = await PaymentKit.OpenAsync(store, tenant);
        var template = PaymentKit.Intents(run)[0];
        var held = new List<string>();
        for (var i = 1; i <= holds; i++)
        {
            var id = $"HC-{i}";
            var intent = new PaymentIntent(run, id, i, 1_000 + i, template.SourceBankId, template.SourceAccountId, template.DestBankId,
                template.DestAccountId, new Money("USD", 1_000 * i), SyntheticTruth.FraudMuleRing, new PaymentFeatures(false, SanctionsHit: true));
            Assert.True((await store.SubmitIntentAsync(tenant, Actor, intent, $"c-{i}", Ct)).IsSuccess);
            held.Add(id);
        }
        Assert.Equal(holds, (await store.ScreenBatchAsync(tenant, 50, "c-screen", Ct)).Count(o => o.Status == PaymentStatus.Held));
        return (store, tenant, run, held);
    }

    [Fact]
    public async Task Closing_serializes_with_concurrent_review_actions_and_no_fact_commits_after_the_closing_point()
    {
        for (var round = 0; round < 3; round++)
        {
            var (store, tenant, run, held) = await HeldRunAsync(8);
            var baseline = await PaymentKit.Scalar<long>(db.Owner, "SELECT count(*) FROM revenant.finance_fact WHERE scenario_run_id = @r", ("r", run.Value));
            var versions = new Dictionary<string, long>();
            foreach (var id in held)
            {
                versions[id] = (await store.GetCaseAsync(tenant, run, id, Ct))!.Version;
            }

            var gate = new TaskCompletionSource();
            var rejects = held.Select(id => Task.Run(async () =>
            {
                await gate.Task;
                try
                {
                    var receipt = await store.CaseActionAsync(tenant, new CaseActor(PaymentKit.Approver, true, false, true),
                        new CaseCommand(run, id, CaseAction.Reject, versions[id], $"reject-{id}", "ring_confirmed"), "c-reject", Ct);
                    return (id, Committed: receipt.IsSuccess, Refused: false);
                }
                catch (PostgresException ex) when (PaymentLedgerStore.IsRunClosed(ex))
                {
                    return (id, Committed: false, Refused: true);
                }
            })).ToList();
            var closing = Task.Run(async () =>
            {
                await gate.Task;
                await Task.Delay(round * 3); // vary the interleaving between rounds
                return await store.CloseRunAsync(tenant, "system:close", run, "c-close", Ct);
            });
            gate.SetResult();
            var outcomes = await Task.WhenAll(rejects);
            var closure = (await closing).Value;

            var committed = outcomes.Count(o => o.Committed);
            var refused = outcomes.Count(o => o.Refused);
            output.WriteLine($"round {round}: closed at {closure.ClosedFactSequence}, {committed} committed before the close, {refused} refused after it");
            Assert.Equal(held.Count, committed + refused);
            Assert.Equal(baseline + committed, closure.ClosedFactSequence);
            Assert.Equal(closure.ClosedFactSequence, await PaymentKit.Scalar<long>(db.Owner,
                "SELECT count(*) FROM revenant.finance_fact WHERE scenario_run_id = @r", ("r", run.Value)));
            Assert.Equal(closure.ClosedFactSequence, await PaymentKit.Scalar<long>(db.Owner,
                "SELECT last_fact_seq FROM revenant.run_cursor WHERE scenario_run_id = @r", ("r", run.Value)));
            Assert.Equal(0L, await PaymentKit.Scalar<long>(db.Owner,
                "SELECT count(*) FROM revenant.finance_fact WHERE scenario_run_id = @r AND fact_seq > (SELECT closed_fact_seq FROM revenant.run_cursor WHERE scenario_run_id = @r)",
                ("r", run.Value)));
            foreach (var o in outcomes)
            {
                var status = await PaymentKit.Scalar<string>(db.Owner,
                    "SELECT status FROM revenant.payment WHERE scenario_run_id = @r AND intent_id = @i", ("r", run.Value), ("i", o.id));
                var hold = await PaymentKit.Scalar<string>(db.Owner,
                    "SELECT h.status FROM revenant.hold h JOIN revenant.payment p ON p.id = h.payment_id WHERE p.scenario_run_id = @r AND p.intent_id = @i",
                    ("r", run.Value), ("i", o.id));
                // Committed means a whole decision (fact, Rejected, released hold); refused means nothing at all (still Held, hold Active).
                Assert.Equal(o.Committed ? ("Rejected", "Released") : ("Held", "Active"), (status, hold));
            }

            // After the close a further decision is refused for certain, and so is direct SQL that skips the application.
            var remaining = outcomes.FirstOrDefault(o => o.Refused).id;
            if (remaining is not null)
            {
                var ex = await Assert.ThrowsAsync<PostgresException>(() => store.CaseActionAsync(tenant, new CaseActor(PaymentKit.Approver, true, false, true),
                    new CaseCommand(run, remaining, CaseAction.Reject, versions[remaining], $"reject-again-{remaining}", "ring_confirmed"), "c-late", Ct));
                Assert.Equal("RV009", ex.SqlState);
                await AssertWriterRefusedAsync(tenant, run,
                    "UPDATE revenant.payment SET status = 'Rejected', version = version + 1 WHERE scenario_run_id = @r AND intent_id = @i", remaining);
            }
            await AssertWriterRefusedAsync(tenant, run, "UPDATE revenant.run_cursor SET closed_fact_seq = NULL WHERE scenario_run_id = @r", null);
            await AssertWriterRefusedAsync(tenant, run, "UPDATE revenant.run_cursor SET last_fact_seq = last_fact_seq + 1 WHERE scenario_run_id = @r", null);
            Assert.True((await store.CloseRunAsync(tenant, "system:close", run, "c-close-2", Ct)).Value.IsReplay);
            Assert.Equal(closure.ClosedFactSequence, await PaymentKit.Scalar<long>(db.Owner,
                "SELECT count(*) FROM revenant.finance_fact WHERE scenario_run_id = @r", ("r", run.Value)));
        }
    }

    [Fact]
    public async Task A_run_with_payments_in_flight_cannot_be_closed_and_a_closed_run_refuses_every_intake()
    {
        var tenant = $"tcl-{Guid.NewGuid():N}"[..14];
        var store = PaymentKit.Store(db.Writer);
        var run = await PaymentKit.OpenAsync(store, tenant);
        var intents = PaymentKit.Intents(run);
        Assert.True((await store.SubmitIntentAsync(tenant, Actor, intents[0], "c1", Ct)).IsSuccess);
        Assert.Equal(PaymentFailureCode.RunNotQuiescent, (await store.CloseRunAsync(tenant, Actor, run, "c-close", Ct)).Error.Code);
        Assert.Equal(PaymentFailureCode.RunNotFound, (await store.CloseRunAsync(tenant, Actor, new ScenarioRunId(Guid.NewGuid()), "c-close", Ct)).Error.Code);
        Assert.Equal(PaymentFailureCode.RunNotFound, (await store.CloseRunAsync("another-tenant", Actor, run, "c-close", Ct)).Error.Code);

        await store.ScreenBatchAsync(tenant, 10, "c-screen", Ct);
        await store.SettleBatchAsync(tenant, "system:settlement", 10, "c-settle", Ct);
        var status = (await store.GetPaymentAsync(tenant, run, intents[0].IntentId, Ct))!.Status;
        output.WriteLine($"first intent is {status}");
        var closed = await store.CloseRunAsync(tenant, Actor, run, "c-close", Ct);
        Assert.True(closed.IsSuccess, closed.IsSuccess ? "" : closed.Error.Message);
        var facts = await PaymentKit.Scalar<long>(db.Owner, "SELECT count(*) FROM revenant.finance_fact WHERE scenario_run_id = @r", ("r", run.Value));
        Assert.Equal(facts, closed.Value.ClosedFactSequence);

        // Replayed intake of a known intent is answered from the record; a new intent is refused and leaves nothing behind.
        Assert.True((await store.SubmitIntentAsync(tenant, Actor, intents[0], "c1-again", Ct)).Value.IsReplay);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => store.SubmitIntentAsync(tenant, Actor, intents[1], "c2", Ct));
        Assert.Equal("RV009", ex.SqlState);
        Assert.Equal((facts, 1L), (await PaymentKit.Scalar<long>(db.Owner, "SELECT count(*) FROM revenant.finance_fact WHERE scenario_run_id = @r", ("r", run.Value)),
            await PaymentKit.Scalar<long>(db.Owner, "SELECT count(*) FROM revenant.payment WHERE scenario_run_id = @r", ("r", run.Value))));
        Assert.Equal(1L, await PaymentKit.Scalar<long>(db.Owner,
            "SELECT count(*) FROM revenant.audit_event WHERE scenario_run_id = @r AND event_type = 'run.closed'", ("r", run.Value)));
    }

    private async Task AssertWriterRefusedAsync(string tenant, ScenarioRunId run, string sql, string? intent)
    {
        await using var connection = await db.Writer.OpenConnectionAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('revenant.tenant_id', @t, true)", connection, tx))
        {
            set.Parameters.AddWithValue("t", tenant);
            await set.ExecuteNonQueryAsync();
        }
        await using var cmd = new NpgsqlCommand(sql, connection, tx);
        cmd.Parameters.AddWithValue("r", run.Value);
        if (intent is not null)
        {
            cmd.Parameters.AddWithValue("i", intent);
        }
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("RV009", ex.SqlState);
    }
}
