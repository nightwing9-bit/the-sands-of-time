using FsCheck;
using FsCheck.Fluent;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Payments;
using Revenant.Scenario;
using Revenant.Tests.Ledger;
using Xunit;
using Xunit.Abstractions;
using static Revenant.Tests.Payments.PaymentKit;
using LedgerSql = Revenant.Tests.Ledger.Ledger;

namespace Revenant.Tests.Payments;

/// <summary>
/// Spec 07 task 7.3 (R07.03, R07.04, R07.06, R07.09, R07.11): holds reserve liquidity without moving posted balances and
/// resolve once; batch settlement over a hot account by two concurrent workers settles each payment once and never
/// overspends; insufficient funds follow the V1 rule; a settled payment is returned only by a new compensating group;
/// the settlement loop survives a database outage. Every check runs on real PostgreSQL through the least-privileged writer.
/// </summary>
[Trait("Category", "Integration")]
[Collection(PaymentCollection.Name)]
public sealed class SettlementTests(LedgerDatabase db, ITestOutputHelper output)
{
    // Friday Ring opening balances: customers 5,000,000 minor units, mules 1,000,000.
    private static readonly string Hot = FridayRingScenario.CustomerAccountId(0, 1);
    private static readonly string Mule = FridayRingScenario.MuleAccountId(0);
    private static readonly string[] Destinations =
        [FridayRingScenario.CustomerAccountId(1, 1), FridayRingScenario.CustomerAccountId(2, 2), FridayRingScenario.CustomerAccountId(3, 1)];

    [Fact]
    public async Task A_hold_lowers_available_but_not_posted_and_is_resolved_exactly_once()
    {
        var kit = await RunKit.OpenAsync(db);
        var opening = await kit.BalanceAsync(Hot);

        // Hold -> reject: reservation placed, then released once.
        var p1 = await kit.SubmitAsync(Hot, Destinations[0], 1_000_000);
        await kit.StepAsync(p1, PaymentStatus.Screened);
        await kit.StepAsync(p1, PaymentStatus.Held, "velocity");
        var held = await kit.BalanceAsync(Hot);
        Assert.Equal((opening.Posted, 1_000_000L, opening.Posted - 1_000_000), held);
        var bank = (await kit.Store.GetRunAsync(kit.Tenant, kit.Run, default))!.Banks.Single(b => b.BankId == "BANK-A");
        Assert.Equal((1_000_000L, bank.PostedMinor - 1_000_000), (bank.ReservedMinor, bank.AvailableMinor));
        Assert.Equal("Active", await kit.HoldStatusAsync(p1));

        var reject = TransitionOf(kit, p1, PaymentStatus.Rejected, 3, "reject-p1");
        Assert.True((await kit.Store.TransitionAsync(kit.Tenant, Operator, reject, "c", default)).IsSuccess);
        var resolvedAt = await Scalar<DateTime>(db.Owner, "SELECT resolved_at FROM revenant.hold WHERE payment_id = @p", ("p", await kit.IdAsync(p1)));
        var again = await kit.Store.TransitionAsync(kit.Tenant, Operator, reject, "c", default);
        Assert.True(again.Value.IsReplay);
        Assert.Equal(opening, await kit.BalanceAsync(Hot));
        Assert.Equal("Released", await kit.HoldStatusAsync(p1));
        Assert.Equal(resolvedAt, await Scalar<DateTime>(db.Owner, "SELECT resolved_at FROM revenant.hold WHERE payment_id = @p", ("p", await kit.IdAsync(p1))));
        Assert.Equal(2L, await kit.EventsAsync("hold:%", await kit.IdAsync(p1)));

        // Hold -> approve -> clear keeps the reservation until settlement consumes it with the posting.
        var p2 = await kit.SubmitAsync(Hot, Destinations[1], 700_000);
        await kit.StepAsync(p2, PaymentStatus.Screened);
        await kit.StepAsync(p2, PaymentStatus.Held);
        await kit.StepAsync(p2, PaymentStatus.Approved);
        await kit.StepAsync(p2, PaymentStatus.Cleared);
        Assert.Equal((opening.Posted, 700_000L, opening.Posted - 700_000), await kit.BalanceAsync(Hot));
        var destBefore = await kit.BalanceAsync(Destinations[1]);

        var batch = await kit.Store.SettleBatchAsync(kit.Tenant, "worker:test", 10, "c", default);
        var outcome = Assert.Single(batch.Outcomes);
        Assert.Equal((p2, PaymentStatus.Settled, 6L), (outcome.IntentId, outcome.Status, outcome.Version));
        Assert.Equal((opening.Posted - 700_000, 0L, opening.Posted - 700_000), await kit.BalanceAsync(Hot));
        Assert.Equal(destBefore.Posted + 700_000, (await kit.BalanceAsync(Destinations[1])).Posted);
        Assert.Equal("Consumed", await kit.HoldStatusAsync(p2));
        Assert.Equal(1L, await Scalar<long>(db.Owner,
            "SELECT count(*) FROM revenant.outbox WHERE event_type = 'revenant.ledger.posted' AND payload::jsonb ->> 'groupId' = @g",
            ("g", outcome.LedgerGroupId!.Value.ToString())));
        Assert.Empty((await kit.Store.SettleBatchAsync(kit.Tenant, "worker:test", 10, "c", default)).Outcomes);

        // The database refuses every other hold mutation, for the writer and for the owner.
        var p2Id = await kit.IdAsync(p2);
        await Denied(db.Writer, kit.Tenant, "UPDATE revenant.hold SET status = 'Active', resolved_at = NULL WHERE payment_id = @p", "RV001", p2Id);
        await Denied(db.Writer, kit.Tenant, "UPDATE revenant.hold SET status = 'Released' WHERE payment_id = @p", "RV001", p2Id);
        await Denied(db.Writer, kit.Tenant, "UPDATE revenant.hold SET amount_minor = 1 WHERE payment_id = @p", "42501", p2Id);
        await Denied(db.Writer, kit.Tenant, "DELETE FROM revenant.hold WHERE payment_id = @p", "42501", p2Id);
        await Denied(db.Owner, kit.Tenant, "DELETE FROM revenant.hold WHERE payment_id = @p", "RV001", p2Id);
        await Denied(db.Writer, kit.Tenant, "UPDATE revenant.account SET id = id WHERE id = (SELECT source_account_id FROM revenant.payment WHERE id = @p)", "RV001", p2Id);
        await Denied(db.Writer, kit.Tenant,
            "INSERT INTO revenant.hold (tenant_id, scenario_run_id, payment_id, account_id, currency, amount_minor) " +
            "SELECT tenant_id, scenario_run_id, id, source_account_id, currency, amount_minor FROM revenant.payment WHERE id = @p", "23505", p2Id);
        await Denied(db.Replay, kit.Tenant, "UPDATE revenant.hold SET status = 'Released' WHERE payment_id = @p", "42501", p2Id);
    }

    [Fact]
    public async Task Insufficient_available_follows_the_V1_rule_and_the_database_refuses_overdrafts_and_unlinked_holds()
    {
        var kit = await RunKit.OpenAsync(db);
        var mule = await kit.BalanceAsync(Mule);
        Assert.Equal(1_000_000L, mule.Available);

        // Hold that does not fit: Held then Rejected (insufficient_available) in one command, nothing reserved.
        var big = await kit.SubmitAsync(Mule, Destinations[0], 1_500_000);
        await kit.StepAsync(big, PaymentStatus.Screened);
        var receipt = await kit.Store.TransitionAsync(kit.Tenant, Operator, TransitionOf(kit, big, PaymentStatus.Held, 2, "hold-big"), "c", default);
        Assert.Equal((PaymentStatus.Rejected, 4L), (receipt.Value.AppliedStatus, receipt.Value.AppliedVersion));
        Assert.Equal([PaymentStatus.Initiated, PaymentStatus.Screened, PaymentStatus.Held, PaymentStatus.Rejected],
            receipt.Value.Payment.Trail.Select(t => t.To));
        Assert.Equal(PaymentLedgerStore.ReasonInsufficientAvailable, receipt.Value.Payment.Trail[^1].Reason);
        Assert.Null(await kit.HoldStatusAsync(big));
        Assert.Equal(mule, await kit.BalanceAsync(Mule));

        // Cleared payment that does not fit at settlement: Returned (insufficient_available), no posting group.
        var cleared = await kit.SubmitAsync(Mule, Destinations[0], 1_000_001);
        await kit.StepAsync(cleared, PaymentStatus.Screened);
        await kit.StepAsync(cleared, PaymentStatus.Cleared);
        var outcome = Assert.Single((await kit.Store.SettleBatchAsync(kit.Tenant, "worker:test", 10, "c", default)).Outcomes);
        Assert.Equal((PaymentStatus.Returned, (Guid?)null), (outcome.Status, outcome.LedgerGroupId));
        Assert.Equal(0L, await Scalar<long>(db.Owner, "SELECT count(*) FROM revenant.ledger_group WHERE payment_id = @p", ("p", outcome.PaymentId)));
        Assert.Equal(mule, await kit.BalanceAsync(Mule));

        // Direct SQL as the writer, skipping every application check: the database still refuses.
        var muleId = await kit.AccountIdAsync(Mule);
        var destId = await kit.AccountIdAsync(Destinations[0]);
        var seeded = new SeededRun(kit.Tenant, kit.Run.Value, new Dictionary<string, Guid>(), new Dictionary<string, Guid>(), new Dictionary<string, long>());

        await using (var conn = await db.Writer.OpenConnectionAsync())
        await using (var tx = await LedgerSql.BeginTenantAsync(conn, kit.Tenant))
        {
            var p = await LedgerSql.InsertPaymentAsync(conn, seeded, "SQL-1", muleId, destId, 1_000_001);
            await LedgerSql.AdvanceAsync(conn, p, "Screened", "Held");
            Assert.Equal("RV005", (await Assert.ThrowsAsync<PostgresException>(() => InsertHoldAsync(conn, p))).SqlState);
        }
        await using (var conn = await db.Writer.OpenConnectionAsync())
        await using (var tx = await LedgerSql.BeginTenantAsync(conn, kit.Tenant))
        {
            var p = await LedgerSql.InsertPaymentAsync(conn, seeded, "SQL-2", muleId, destId, 1_000_001);
            await LedgerSql.AdvanceAsync(conn, p, "Screened", "Cleared", "Settled");
            var group = await LedgerSql.InsertGroupAsync(conn, seeded, p, "Settlement");
            Assert.Equal("RV005", (await Assert.ThrowsAsync<PostgresException>(() =>
                LedgerSql.InsertLegAsync(conn, seeded, group, muleId, "Credit", 1_000_001))).SqlState);
        }
        await using (var conn = await db.Writer.OpenConnectionAsync())
        await using (var tx = await LedgerSql.BeginTenantAsync(conn, kit.Tenant))
        {
            // Held without its reservation cannot commit.
            var p = await LedgerSql.InsertPaymentAsync(conn, seeded, "SQL-3", muleId, destId, 10);
            await LedgerSql.AdvanceAsync(conn, p, "Screened", "Held");
            Assert.Equal("RV006", (await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync())).SqlState);
        }
        await using (var conn = await db.Writer.OpenConnectionAsync())
        await using (var tx = await LedgerSql.BeginTenantAsync(conn, kit.Tenant))
        {
            // Releasing a reservation while the payment is still Held cannot commit.
            var p = await LedgerSql.InsertPaymentAsync(conn, seeded, "SQL-4", muleId, destId, 10);
            await LedgerSql.AdvanceAsync(conn, p, "Screened", "Held");
            await InsertHoldAsync(conn, p);
            await LedgerSql.ExecAsync(conn, "UPDATE revenant.hold SET status = 'Released', resolved_at = now() WHERE payment_id = @p", ("p", p));
            Assert.Equal("RV006", (await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync())).SqlState);
        }
        await using (var conn = await db.Writer.OpenConnectionAsync())
        await using (var tx = await LedgerSql.BeginTenantAsync(conn, kit.Tenant))
        {
            // Rejecting a held payment without unwinding its reservation cannot commit.
            var p = await LedgerSql.InsertPaymentAsync(conn, seeded, "SQL-5", muleId, destId, 10);
            await LedgerSql.AdvanceAsync(conn, p, "Screened", "Held");
            await InsertHoldAsync(conn, p);
            await LedgerSql.AdvanceAsync(conn, p, "Rejected");
            Assert.Equal("RV006", (await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync())).SqlState);
        }
        Assert.Equal(mule, await kit.BalanceAsync(Mule));
    }

    [Fact]
    public async Task Two_workers_on_one_hot_account_settle_each_payment_once_and_never_overspend()
    {
        var kit = await RunKit.OpenAsync(db);
        var totalOpening = await kit.TotalPostedAsync();
        var opening = await kit.BalanceAsync(Hot);
        Assert.Equal(5_000_000L, opening.Available);

        // Three held payments reserve 600,000 first; then 25 cleared payments ask for 5,000,000 more.
        var held = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var id = await kit.SubmitAsync(Hot, Destinations[i % 3], 200_000);
            await kit.StepAsync(id, PaymentStatus.Screened);
            await kit.StepAsync(id, PaymentStatus.Held);
            held.Add(id);
        }
        var cleared = new List<string>();
        for (var i = 0; i < 25; i++)
        {
            var id = await kit.SubmitAsync(Hot, Destinations[i % 3], 200_000);
            await kit.StepAsync(id, PaymentStatus.Screened);
            await kit.StepAsync(id, PaymentStatus.Cleared);
            cleared.Add(id);
        }

        var (first, second) = await TwoWorkersAsync(kit.Store, kit.Tenant, batchSize: 3);
        output.WriteLine($"round 1: worker A resolved {first.Count}, worker B resolved {second.Count}");
        Assert.Equal(25, first.Count + second.Count);
        Assert.Empty(first.Select(o => o.PaymentId).Intersect(second.Select(o => o.PaymentId)));
        var statuses = await kit.StatusesAsync();
        Assert.Equal(22, cleared.Count(c => statuses[c] == PaymentStatus.Settled));
        Assert.Equal(3, cleared.Count(c => statuses[c] == PaymentStatus.Returned));
        Assert.All(held, h => Assert.Equal(PaymentStatus.Held, statuses[h]));
        // The reservations were never spent by the cleared payments.
        Assert.Equal((600_000L, 600_000L, 0L), await kit.BalanceAsync(Hot));

        foreach (var id in held)
        {
            await kit.StepAsync(id, PaymentStatus.Approved);
            await kit.StepAsync(id, PaymentStatus.Cleared);
        }
        var (third, fourth) = await TwoWorkersAsync(kit.Store, kit.Tenant, batchSize: 1);
        output.WriteLine($"round 2: worker A resolved {third.Count}, worker B resolved {fourth.Count}");
        statuses = await kit.StatusesAsync();
        Assert.All(held, h => Assert.Equal(PaymentStatus.Settled, statuses[h]));
        Assert.Equal((0L, 0L, 0L), await kit.BalanceAsync(Hot));

        // Conservation and single settlement, from the database.
        Assert.Equal(totalOpening, await kit.TotalPostedAsync());
        Assert.Equal(0L, await Scalar<long>(db.Owner,
            "SELECT count(*) FROM revenant.account_available_balance WHERE scenario_run_id = @r AND (posted_minor < 0 OR available_minor < 0)", ("r", kit.Run.Value)));
        Assert.Equal(25L, await Scalar<long>(db.Owner,
            "SELECT count(*) FROM revenant.ledger_group WHERE scenario_run_id = @r AND purpose = 'Settlement'", ("r", kit.Run.Value)));
        Assert.Equal(0L, await Scalar<long>(db.Owner,
            "SELECT count(*) FROM (SELECT payment_id FROM revenant.ledger_group WHERE scenario_run_id = @r GROUP BY payment_id HAVING count(*) > 1) d", ("r", kit.Run.Value)));
        Assert.Equal(50L, await Scalar<long>(db.Owner, "SELECT count(*) FROM revenant.ledger_entry WHERE scenario_run_id = @r", ("r", kit.Run.Value)));
        Assert.Equal(25L, await Scalar<long>(db.Owner,
            "SELECT count(*) FROM revenant.outbox o JOIN revenant.ledger_group g ON o.aggregate_id = 'ledger:' || replace(g.id::text, '-', '') WHERE g.scenario_run_id = @r",
            ("r", kit.Run.Value)));
        Assert.Equal(3L, await Scalar<long>(db.Owner,
            "SELECT count(*) FROM revenant.hold WHERE scenario_run_id = @r AND status = 'Consumed'", ("r", kit.Run.Value)));
        // Every trail is contiguous: one row per version, versions 1..n.
        Assert.Equal(0L, await Scalar<long>(db.Owner,
            "SELECT count(*) FROM revenant.payment p WHERE p.scenario_run_id = @r AND p.version <> (SELECT count(*) FROM revenant.payment_transition t WHERE t.payment_id = p.id)",
            ("r", kit.Run.Value)));
        Assert.Empty((await kit.Store.SettleBatchAsync(kit.Tenant, "worker:test", 50, "c", default)).Outcomes);
    }

    [Fact]
    public async Task The_queue_claim_skips_a_locked_payment_instead_of_waiting_for_it()
    {
        var kit = await RunKit.OpenAsync(db);
        var a = await kit.SubmitAsync(Hot, Destinations[0], 100);
        var b = await kit.SubmitAsync(Hot, Destinations[1], 100);
        foreach (var id in new[] { a, b })
        {
            await kit.StepAsync(id, PaymentStatus.Screened);
            await kit.StepAsync(id, PaymentStatus.Cleared);
        }

        await using (var conn = await db.Writer.OpenConnectionAsync())
        await using (var tx = await LedgerSql.BeginTenantAsync(conn, kit.Tenant))
        {
            await LedgerSql.ExecAsync(conn, "SELECT 1 FROM revenant.payment WHERE id = @p FOR UPDATE", ("p", await kit.IdAsync(a)));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var batch = await kit.Store.SettleBatchAsync(kit.Tenant, "worker:test", 10, "c", timeout.Token);
            Assert.Equal([b], batch.Outcomes.Select(o => o.IntentId));
        }
        Assert.Equal([a], (await kit.Store.SettleBatchAsync(kit.Tenant, "worker:test", 10, "c", default)).Outcomes.Select(o => o.IntentId));
    }

    [Fact]
    public async Task A_settled_payment_is_returned_only_by_one_new_mirror_group_and_only_while_the_destination_can_fund_it()
    {
        var kit = await RunKit.OpenAsync(db);
        var opening = await kit.BalanceAsync(Hot);
        var p = await kit.SubmitAsync(Hot, Destinations[0], 300_000);
        await kit.StepAsync(p, PaymentStatus.Screened);
        await kit.StepAsync(p, PaymentStatus.Cleared);
        var settled = Assert.Single((await kit.Store.SettleBatchAsync(kit.Tenant, "worker:test", 10, "c", default)).Outcomes);
        var original = await EntriesAsync(settled.LedgerGroupId!.Value);

        var notSettled = await kit.SubmitAsync(Hot, Destinations[0], 1);
        var illegal = await kit.Store.ReturnSettledAsync(kit.Tenant, Operator, new ReturnCommand(kit.Run, notSettled, 1, "ret-x", null), "c", default);
        Assert.Equal(PaymentFailureCode.IllegalTransition, illegal.Error.Code);

        // Six concurrent copies of one return command: one effect.
        var command = new ReturnCommand(kit.Run, p, 4, "ret-1", "customer recall");
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            Task.Run(() => kit.Store.ReturnSettledAsync(kit.Tenant, Operator, command, "c", default))));
        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal(1, results.Count(r => !r.Value.IsReplay));
        Assert.Equal((PaymentStatus.Returned, 5L), (results[0].Value.AppliedStatus, results[0].Value.AppliedVersion));
        Assert.Equal(opening, await kit.BalanceAsync(Hot));
        Assert.Equal(original, await EntriesAsync(settled.LedgerGroupId!.Value));
        Assert.Equal(1L, await Scalar<long>(db.Owner,
            "SELECT count(*) FROM revenant.ledger_group WHERE payment_id = @p AND purpose = 'Return' AND compensates_group_id = @g",
            ("p", settled.PaymentId), ("g", settled.LedgerGroupId!.Value)));

        // The destination spends the money; a return can no longer be funded and is refused (recorded, repeatable).
        var q = await kit.SubmitAsync(Hot, Mule, 400_000);
        await kit.StepAsync(q, PaymentStatus.Screened);
        await kit.StepAsync(q, PaymentStatus.Cleared);
        var drain = await kit.SubmitAsync(Mule, Destinations[1], 1_400_000);
        Assert.Single((await kit.Store.SettleBatchAsync(kit.Tenant, "worker:test", 10, "c", default)).Outcomes, o => o.Status == PaymentStatus.Settled);
        await kit.StepAsync(drain, PaymentStatus.Screened);
        await kit.StepAsync(drain, PaymentStatus.Cleared);
        Assert.Equal(PaymentStatus.Settled, Assert.Single((await kit.Store.SettleBatchAsync(kit.Tenant, "worker:test", 10, "c", default)).Outcomes).Status);
        var refused = new ReturnCommand(kit.Run, q, 4, "ret-q", null);
        Assert.Equal(PaymentFailureCode.InsufficientAvailable,
            (await kit.Store.ReturnSettledAsync(kit.Tenant, Operator, refused, "c", default)).Error.Code);
        Assert.Equal(PaymentFailureCode.InsufficientAvailable,
            (await kit.Store.ReturnSettledAsync(kit.Tenant, Operator, refused, "c", default)).Error.Code);
        Assert.Equal(PaymentStatus.Settled, (await kit.StatusesAsync())[q]);
        Assert.Equal(0L, (await kit.BalanceAsync(Mule)).Posted);
    }

    [Fact]
    public async Task The_settlement_loop_survives_a_database_outage_and_settles_each_payment_once()
    {
        var kit = await RunKit.OpenAsync(db);
        var totalOpening = await kit.TotalPostedAsync();
        var ids = new List<string>();
        for (var i = 0; i < 8; i++)
        {
            var id = await kit.SubmitAsync(Hot, Destinations[i % 3], 10_000);
            await kit.StepAsync(id, PaymentStatus.Screened);
            await kit.StepAsync(id, PaymentStatus.Cleared);
            ids.Add(id);
        }

        var login = await Scalar<string>(db.Writer, "SELECT current_user");
        await Exec(db.Owner, $"ALTER ROLE {login} NOLOGIN");
        await Exec(db.Owner, $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE usename = '{login}'");
        var worker = new SettlementWorker(kit.Store, new RevenantLedgerOptions { TenantId = kit.Tenant },
            new SettlementWorkerOptions { BatchSize = 3, IdleDelayMs = 20, MaxBackoffMs = 200 }, NullLogger<SettlementWorker>.Instance);
        try
        {
            await worker.StartAsync(default);
            await WaitUntilAsync(() => worker.Failures >= 3, "the worker to observe the outage");
            Assert.Equal(0L, worker.Resolved);
            Assert.All((await kit.StatusesAsync()).Where(s => ids.Contains(s.Key)), s => Assert.Equal(PaymentStatus.Cleared, s.Value));
        }
        finally
        {
            await Exec(db.Owner, $"ALTER ROLE {login} LOGIN");
        }
        await WaitUntilAsync(() => worker.Resolved >= ids.Count, "the worker to settle after recovery");
        await worker.StopAsync(default);
        output.WriteLine($"outage: {worker.Failures} failed attempts, then {worker.Resolved} payments settled");

        var statuses = await kit.StatusesAsync();
        Assert.All(ids, id => Assert.Equal(PaymentStatus.Settled, statuses[id]));
        Assert.Equal(ids.Count, worker.Resolved);
        Assert.Equal((long)ids.Count, await Scalar<long>(db.Owner,
            "SELECT count(*) FROM revenant.ledger_group WHERE scenario_run_id = @r", ("r", kit.Run.Value)));
        Assert.Equal(totalOpening, await kit.TotalPostedAsync());
    }

    /// <summary>
    /// Property (R07.04, R07.06, R07.11): random payments from one account (mixed hold/reject, hold/return, hold/approve/clear
    /// and direct clear paths, amounts that often exceed the balance) end in exactly the statuses and balances of an
    /// independent liquidity model of the V1 rule; posted totals are conserved and nothing stays reserved.
    /// </summary>
    [Fact]
    public void Random_payment_paths_match_an_independent_liquidity_model()
    {
        var payment = from amount in Gen.Choose(1, 2_500) from path in Gen.Choose(0, 3) select (Amount: amount * 1_000L, Path: path);
        var cases = from n in Gen.Choose(1, 7) from list in Gen.ArrayOf(payment, n) select list;
        var observed = new Dictionary<string, int>();

        Prop.ForAll(cases.ToArbitrary(), payments =>
        {
            var kit = RunKit.OpenAsync(db).GetAwaiter().GetResult();
            var totalOpening = kit.TotalPostedAsync().GetAwaiter().GetResult();
            long available = 1_000_000; // mule opening
            var expected = new List<(string Id, PaymentStatus Status)>();
            var model = new List<(string Id, long Amount, int Path, bool Reserved, PaymentStatus Status)>();

            // Phase 1: screen, then hold or clear, in intent order.
            for (var i = 0; i < payments.Length; i++)
            {
                var (amount, path) = payments[i];
                var id = kit.SubmitAsync(Mule, Destinations[i % 3], amount).GetAwaiter().GetResult();
                kit.StepAsync(id, PaymentStatus.Screened).GetAwaiter().GetResult();
                if (path == 3)
                {
                    kit.StepAsync(id, PaymentStatus.Cleared).GetAwaiter().GetResult();
                    model.Add((id, amount, path, false, PaymentStatus.Cleared));
                    continue;
                }
                var receipt = kit.StepAsync(id, PaymentStatus.Held).GetAwaiter().GetResult();
                var fits = available >= amount;
                if (fits)
                {
                    available -= amount;
                }
                model.Add((id, amount, path, fits, fits ? PaymentStatus.Held : PaymentStatus.Rejected));
                if (receipt.AppliedStatus != (fits ? PaymentStatus.Held : PaymentStatus.Rejected))
                {
                    return false;
                }
            }
            // Phase 2: resolve the reservations.
            for (var i = 0; i < model.Count; i++)
            {
                var m = model[i];
                if (m.Status != PaymentStatus.Held)
                {
                    continue;
                }
                switch (m.Path)
                {
                    case 0:
                        kit.StepAsync(m.Id, PaymentStatus.Rejected).GetAwaiter().GetResult();
                        available += m.Amount;
                        model[i] = m with { Reserved = false, Status = PaymentStatus.Rejected };
                        break;
                    case 1:
                        kit.StepAsync(m.Id, PaymentStatus.Returned).GetAwaiter().GetResult();
                        available += m.Amount;
                        model[i] = m with { Reserved = false, Status = PaymentStatus.Returned };
                        break;
                    default:
                        kit.StepAsync(m.Id, PaymentStatus.Approved).GetAwaiter().GetResult();
                        kit.StepAsync(m.Id, PaymentStatus.Cleared).GetAwaiter().GetResult();
                        model[i] = m with { Status = PaymentStatus.Cleared };
                        break;
                }
            }
            // Phase 3: one settlement batch in intent order.
            long settledTotal = 0;
            for (var i = 0; i < model.Count; i++)
            {
                var m = model[i];
                if (m.Status != PaymentStatus.Cleared)
                {
                    continue;
                }
                var own = m.Reserved ? m.Amount : 0;
                if (available + own >= m.Amount)
                {
                    available = available + own - m.Amount;
                    settledTotal += m.Amount;
                    model[i] = m with { Status = PaymentStatus.Settled };
                }
                else
                {
                    available += own;
                    model[i] = m with { Status = PaymentStatus.Returned };
                }
            }
            kit.Store.SettleBatchAsync(kit.Tenant, "worker:test", 50, "c", default).GetAwaiter().GetResult();

            var statuses = kit.StatusesAsync().GetAwaiter().GetResult();
            var mule = kit.BalanceAsync(Mule).GetAwaiter().GetResult();
            foreach (var m in model)
            {
                observed[m.Status.ToString()] = observed.GetValueOrDefault(m.Status.ToString()) + 1;
            }
            return model.All(m => statuses[m.Id] == m.Status)
                && mule == (1_000_000 - settledTotal, 0L, 1_000_000 - settledTotal)
                && available == mule.Available
                && kit.TotalPostedAsync().GetAwaiter().GetResult() == totalOpening;
        }).Check(Config.QuickThrowOnFailure.WithMaxTest(25));

        output.WriteLine("final statuses across generated cases: " + string.Join(", ", observed.OrderBy(o => o.Key).Select(o => $"{o.Key}={o.Value}")));
        Assert.True(observed.GetValueOrDefault("Settled") > 0 && observed.GetValueOrDefault("Rejected") > 0
            && observed.GetValueOrDefault("Returned") > 0, "the generator must reach settled, rejected and returned outcomes");
    }

    // ------------------------------------------------------------------------------------------------------------

    private static TransitionCommand TransitionOf(RunKit kit, string intent, PaymentStatus target, long expected, string id) =>
        new(kit.Run, intent, target, expected, id, null);

    private static async Task<(List<SettlementOutcome> A, List<SettlementOutcome> B)> TwoWorkersAsync(PaymentLedgerStore store, string tenant, int batchSize)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<List<SettlementOutcome>> Worker(string name)
        {
            await start.Task;
            var resolved = new List<SettlementOutcome>();
            while (true)
            {
                var batch = await store.SettleBatchAsync(tenant, name, batchSize, "c", default);
                if (batch.Outcomes.Count == 0)
                {
                    return resolved;
                }
                resolved.AddRange(batch.Outcomes);
            }
        }
        var a = Task.Run(() => Worker("worker:a"));
        var b = Task.Run(() => Worker("worker:b"));
        start.SetResult();
        return (await a, await b);
    }

    private static Task<int> InsertHoldAsync(NpgsqlConnection conn, Guid payment) =>
        LedgerSql.ExecAsync(conn,
            "INSERT INTO revenant.hold (tenant_id, scenario_run_id, payment_id, account_id, currency, amount_minor) " +
            "SELECT tenant_id, scenario_run_id, id, source_account_id, currency, amount_minor FROM revenant.payment WHERE id = @p",
            ("p", payment));

    private async Task<string> EntriesAsync(Guid group) =>
        await Scalar<string>(db.Owner,
            "SELECT string_agg(id::text || direction || amount_minor || created_at::text, ',' ORDER BY id) FROM revenant.ledger_entry WHERE group_id = @g",
            ("g", group));

    private static async Task Denied(NpgsqlDataSource source, string tenant, string sql, string sqlState, Guid payment)
    {
        await using var conn = await source.OpenConnectionAsync();
        await using var tx = await LedgerSql.BeginTenantAsync(conn, tenant);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => LedgerSql.ExecAsync(conn, sql, ("p", payment)));
        Assert.True(sqlState == ex.SqlState, $"{sql}: expected {sqlState}, got {ex.SqlState} ({ex.MessageText})");
    }

    private static async Task Exec(NpgsqlDataSource source, string sql)
    {
        await using var cmd = source.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for {what}");
            await Task.Delay(25);
        }
    }

    /// <summary>One opened Friday Ring run plus helpers that submit custom ordered intents and step them through the store.</summary>
    private sealed class RunKit
    {
        private readonly LedgerDatabase _db;
        private readonly Dictionary<string, string> _banks;
        private long _sequence;

        private RunKit(LedgerDatabase db, PaymentLedgerStore store, string tenant, ScenarioRunId run, Dictionary<string, string> banks) =>
            (_db, Store, Tenant, Run, _banks) = (db, store, tenant, run, banks);

        public PaymentLedgerStore Store { get; }

        /// <summary>A tenant of its own, so the tenant-wide settlement queue holds only this kit's payments (RLS-isolated).</summary>
        public string Tenant { get; }
        public ScenarioRunId Run { get; }

        public static async Task<RunKit> OpenAsync(LedgerDatabase db)
        {
            var store = PaymentKit.Store(db.Writer);
            var tenant = $"t-{Guid.NewGuid():N}";
            var run = await PaymentKit.OpenAsync(store, tenant: tenant);
            var manifest = FridayRingScenario.Create(42, ScenarioSchemas.FridayRingVersion, run).Value.Manifest;
            return new RunKit(db, store, tenant, run, manifest.Accounts.ToDictionary(a => a.AccountId, a => a.BankId));
        }

        public async Task<string> SubmitAsync(string source, string dest, long amount)
        {
            var seq = ++_sequence;
            var intent = new PaymentIntent(Run, $"T-{seq:D4}", seq, seq * 1_000, _banks[source], source, _banks[dest], dest,
                new Money("USD", amount), SyntheticTruth.Legitimate, new PaymentFeatures(false, false));
            var receipt = await Store.SubmitIntentAsync(Tenant, Operator, intent, "c", default);
            return receipt.IsSuccess ? intent.IntentId : throw new InvalidOperationException($"intake failed: {receipt.Error}");
        }

        public async Task<TransitionReceipt> StepAsync(string intent, PaymentStatus target, string? reason = null)
        {
            var payment = (await Store.GetPaymentAsync(Tenant, Run, intent, default))!;
            if (payment.Status == PaymentStatus.Held && target == PaymentStatus.Approved)
            {
                // Since task 7.4 a held payment is released only through its review case (four-eyes when high-value).
                await PaymentKit.ReleaseThroughCaseAsync(Store, Tenant, Run, intent);
                var released = (await Store.GetPaymentAsync(Tenant, Run, intent, default))!;
                return new TransitionReceipt(released, released.Status, released.Version, IsReplay: false);
            }
            var version = payment.Version;
            var result = await Store.TransitionAsync(Tenant, Operator,
                new TransitionCommand(Run, intent, target, version, $"{intent}-{target}-{version}", reason), "c", default);
            return result.IsSuccess ? result.Value : throw new InvalidOperationException($"{intent} -> {target} failed: {result.Error}");
        }

        public Task<Guid> IdAsync(string intent) =>
            Scalar<Guid>(_db.Owner, "SELECT id FROM revenant.payment WHERE scenario_run_id = @r AND intent_id = @i", ("r", Run.Value), ("i", intent));

        public Task<Guid> AccountIdAsync(string local) =>
            Scalar<Guid>(_db.Owner, "SELECT id FROM revenant.account WHERE scenario_run_id = @r AND local_account_id = @a", ("r", Run.Value), ("a", local));

        public async Task<string?> HoldStatusAsync(string intent) =>
            await Scalar<string?>(_db.Owner, "SELECT status FROM revenant.hold WHERE payment_id = @p", ("p", await IdAsync(intent)));

        public async Task<long> EventsAsync(string aggregatePattern, Guid payment) =>
            await Scalar<long>(_db.Owner,
                "SELECT count(*) FROM revenant.outbox WHERE aggregate_id LIKE @a AND payload::jsonb ->> 'paymentId' = @p",
                ("a", aggregatePattern), ("p", payment.ToString()));

        /// <summary>(posted, reserved, available) from the database view, as the migration owner.</summary>
        public async Task<(long Posted, long Reserved, long Available)> BalanceAsync(string local)
        {
            await using var cmd = _db.Owner.CreateCommand(
                "SELECT posted_minor, reserved_minor, available_minor FROM revenant.account_available_balance WHERE scenario_run_id = @r AND local_account_id = @a");
            cmd.Parameters.AddWithValue("r", Run.Value);
            cmd.Parameters.AddWithValue("a", local);
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
        }

        public Task<long> TotalPostedAsync() =>
            Scalar<long>(_db.Owner, "SELECT sum(posted_minor)::bigint FROM revenant.account_posted_balance WHERE scenario_run_id = @r", ("r", Run.Value));

        public async Task<Dictionary<string, PaymentStatus>> StatusesAsync()
        {
            await using var cmd = _db.Owner.CreateCommand("SELECT intent_id, status FROM revenant.payment WHERE scenario_run_id = @r");
            cmd.Parameters.AddWithValue("r", Run.Value);
            var map = new Dictionary<string, PaymentStatus>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                map[reader.GetString(0)] = Enum.Parse<PaymentStatus>(reader.GetString(1));
            }
            return map;
        }
    }
}

/// <summary>The bounded retry only repeats serialization failures and deadlocks, at most <see cref="TransientSql.MaxAttempts"/> times.</summary>
public sealed class TransientSqlTests
{
    [Theory]
    [InlineData("40001", true)]
    [InlineData("40P01", true)]
    [InlineData("23505", false)]
    [InlineData("RV005", false)]
    [InlineData("57P01", false)]
    public async Task Only_serialization_failures_and_deadlocks_are_retried_and_the_retry_is_bounded(string sqlState, bool retried)
    {
        var calls = 0;
        var error = await Assert.ThrowsAsync<PostgresException>(() => TransientSql.RetryAsync<int>(() =>
        {
            calls++;
            throw new PostgresException("simulated", "ERROR", "ERROR", sqlState);
        }, default));
        Assert.Equal(sqlState, error.SqlState);
        Assert.Equal(retried ? TransientSql.MaxAttempts : 1, calls);

        var attempts = 0;
        var value = await TransientSql.RetryAsync(() =>
        {
            attempts++;
            return attempts == 1 && retried ? throw new PostgresException("simulated", "ERROR", "ERROR", sqlState) : Task.FromResult(7);
        }, default);
        Assert.Equal(7, value);
    }
}
