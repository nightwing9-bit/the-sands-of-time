using System.Net;
using System.Net.Sockets;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Marrow.Infrastructure.Hosting;
using Marrow.Infrastructure.Messaging;
using Marrow.Kernel.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Revenant.Cases;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Payments;
using Revenant.Api;
using Revenant.Projection;
using Revenant.Scenario;
using Revenant.Tests.Ledger;
using StackExchange.Redis;
using Xunit;
using Xunit.Abstractions;

namespace Revenant.Tests.Payments;

/// <summary>A real Redis 7.4 (the Compose image, append-only file) for the read-projection tests.</summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private readonly int _port = FreePort();
    private readonly IContainer _redis;

    public RedisFixture() => _redis = new ContainerBuilder("redis:7.4-alpine")
        .WithPortBinding(_port, 6379)
        .WithCommand("redis-server", "--save", "", "--appendonly", "yes", "--appendfsync", "always")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Ready to accept connections"))
        .Build();

    public string ConnectionString => $"127.0.0.1:{_port},abortConnect=false,connectTimeout=2000,syncTimeout=3000,asyncTimeout=3000";

    public Task InitializeAsync() => _redis.StartAsync();

    public async Task DisposeAsync() => await _redis.DisposeAsync();

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

/// <summary>
/// Spec 07 exit review, R07.09: the idempotent read projection of the financial outbox events, over real PostgreSQL 18 and real
/// Redis 7.4 and the production Worker registrations (<c>AddMarrowOutboxRelayService</c> on the <c>sands_worker_ledger</c> login,
/// <c>AddMarrowInboxConsumerService</c> with <see cref="RevenantEventProjection"/> on the <c>sands_worker</c> login, both from the
/// logins <c>migrate.sh</c> provisions). Ledger actions are committed through the store; nothing is published by hand.
/// **Validates: Requirements R07.09**
/// </summary>
[Collection(PaymentCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RevenantEventProjectionTests(LedgerDatabase db, RedisFixture redis, ITestOutputHelper output) : IClassFixture<RedisFixture>
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string Actor = "staff:operator-1";

    [Fact]
    public async Task Committed_payment_hold_and_settlement_actions_reach_the_read_model_once_and_agree_with_the_ledger()
    {
        var logins = await db.ProvisionDeployedLoginsAsync();
        var baseSeq = await BaselineAsync();
        var tenant = $"t709-{Guid.NewGuid():N}"[..16];
        var stream = $"revenant:events:test:{Guid.NewGuid():N}";
        await using var ledgerHost = LedgerHost(logins, tenant);
        var store = ledgerHost.GetRequiredService<PaymentLedgerStore>();

        // Real committed actions: legitimate L (held, released, settled), ring R (held, rejected), ring H (held, four-eyes,
        // settled, then returned by a compensating posting) and a clean N (cleared, settled).
        var run = await PaymentKit.OpenAsync(store, tenant);
        var (bank, account, destBank, dest) = await AccountsAsync(run.Value);
        PaymentIntent Intent(string id, int seq, long amount, SyntheticTruth truth, bool sanctions) => new(
            run, id, seq, 1_000 + seq, bank, account, destBank, dest, new Money("USD", amount), truth, new PaymentFeatures(false, sanctions));
        foreach (var intent in new[]
        {
            Intent("L", 1, 100_000, SyntheticTruth.Legitimate, true), Intent("R", 2, 200_000, SyntheticTruth.FraudMuleRing, true),
            Intent("H", 3, 1_500_000, SyntheticTruth.FraudMuleRing, true), Intent("N", 4, 5_000, SyntheticTruth.Legitimate, false),
        })
        {
            Assert.True((await store.SubmitIntentAsync(tenant, Actor, intent, $"c-{intent.IntentId}", Ct)).IsSuccess);
        }
        Assert.Equal(4, (await store.ScreenBatchAsync(tenant, 20, "c-screen", Ct)).Count);
        await PaymentKit.ReleaseThroughCaseAsync(store, tenant, run, "L");
        await PaymentKit.ReleaseThroughCaseAsync(store, tenant, run, "H");
        var rCase = (await store.GetCaseAsync(tenant, run, "R", Ct))!;
        Assert.True((await store.CaseActionAsync(tenant, new CaseActor(PaymentKit.Approver, true, false, true),
            new CaseCommand(run, "R", CaseAction.Reject, rCase.Version, "reject-R", "ring_confirmed"), "c-reject", Ct)).IsSuccess);
        Assert.Equal(3, (await store.SettleBatchAsync(tenant, "system:settlement", 20, "c-settle", Ct)).Outcomes.Count(o => o.Status == PaymentStatus.Settled));
        var settledH = (await store.GetPaymentAsync(tenant, run, "H", Ct))!;
        Assert.True((await store.ReturnSettledAsync(tenant, Actor, new ReturnCommand(run, "H", settledH.Version, "h-return", "ring_recall"), "c-return", Ct)).IsSuccess);

        // 1. Each action committed its outbox event with the business write; no relay or consumer has run, so nothing is projected.
        Assert.True(await PaymentKit.OutboxRows(db.Owner, run.Value) >= 4 * 2);
        Assert.Equal(0L, await ViewRowsAsync(run.Value));
        Assert.True(await Scalar<long>("SELECT count(*) FROM revenant.outbox WHERE seq > @s AND status = 'pending'", ("s", baseSeq)) > 0);

        // 2. The existing relay delivers them to Redis. The consumer is down, so the entries wait in the stream.
        // The database clock stamps published_at, so the re-publish point comes from it too (the Docker VM clock can differ from the host's).
        var before = new DateTimeOffset(DateTime.SpecifyKind(await Scalar<DateTime>("SELECT now() - interval '1 second'"), DateTimeKind.Utc));
        await using (var relay = Worker(logins, stream, relay: true, consumer: false, "c1"))
        {
            await relay.StartAsync();
            await WaitAsync(async () => await Scalar<long>("SELECT count(*) FROM revenant.outbox WHERE seq > @s AND status <> 'published'", ("s", baseSeq)) == 0,
                "the relay to publish every pending event");
            var published = await Scalar<long>("SELECT count(*) FROM revenant.outbox WHERE seq > @s", ("s", baseSeq));
            using var mux = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
            Assert.Equal(published, await mux.GetDatabase().StreamLengthAsync(stream));
            Assert.Equal(0L, await ViewRowsAsync(run.Value));

            // 3. The consumer (a separate process composition) starts, groups from the stream start, and converges.
            await using var consumer = Worker(logins, stream, relay: false, consumer: true, "c1");
            await consumer.StartAsync();
            await WaitAsync(async () => await UnconsumedAsync(baseSeq) == 0, "the consumer to apply every event");
            await AssertAgreesWithLedgerAsync(run.Value);
            await AssertJourneyOutcomesAsync(run.Value);

            // 4. Repeated delivery: the operator procedure re-publishes everything; the relay re-adds each entry, the consumer
            //    receives every one again and the read model, inbox and row versions do not move.
            var snapshot = await SnapshotAsync(run.Value);
            var inbox = await Scalar<long>("SELECT count(*) FROM revenant.inbox WHERE consumer_id = @c", ("c", RevenantEventProjection.ConsumerId));
            await using var recovery = NpgsqlDataSource.Create(logins.WorkerLedger);
            var requeued = await OutboxRecovery.RepublishSinceAsync(recovery, new MessagingTables("revenant"), before, Ct);
            Assert.True(requeued >= published);
            var db0 = mux.GetDatabase();
            await WaitAsync(async () =>
            {
                var group = (await db0.StreamGroupInfoAsync(stream)).Single(g => g.Name == RevenantEventProjection.ConsumerId);
                return await db0.StreamLengthAsync(stream) >= published + requeued && group.PendingMessageCount == 0 && group.Lag == 0;
            }, "the duplicate deliveries to be consumed and acknowledged");
            output.WriteLine($"published {published}, re-delivered {requeued}, inbox {inbox}");
            Assert.Equal(snapshot, await SnapshotAsync(run.Value));
            Assert.Equal(inbox, await Scalar<long>("SELECT count(*) FROM revenant.inbox WHERE consumer_id = @c", ("c", RevenantEventProjection.ConsumerId)));
            await AssertAgreesWithLedgerAsync(run.Value);
            await AssertJourneyOutcomesAsync(run.Value);
            await consumer.StopAsync();
            await relay.StopAsync();
        }
    }

    [Fact]
    public async Task A_failing_projection_effect_rolls_back_with_its_inbox_row_and_a_restarted_consumer_converges_without_loss()
    {
        var logins = await db.ProvisionDeployedLoginsAsync();
        var baseSeq = await BaselineAsync();
        var tenant = $"t709r-{Guid.NewGuid():N}"[..16];
        var stream = $"revenant:events:test:{Guid.NewGuid():N}";
        await using var ledgerHost = LedgerHost(logins, tenant);
        var store = ledgerHost.GetRequiredService<PaymentLedgerStore>();
        var run = await PaymentKit.OpenAsync(store, tenant);
        var (bank, account, destBank, dest) = await AccountsAsync(run.Value);
        Assert.True((await store.SubmitIntentAsync(tenant, Actor, new PaymentIntent(run, "L", 1, 1_001, bank, account, destBank, dest,
            new Money("USD", 100_000), SyntheticTruth.Legitimate, new PaymentFeatures(false, true)), "c", Ct)).IsSuccess);
        Assert.Equal(PaymentStatus.Held, Assert.Single(await store.ScreenBatchAsync(tenant, 5, "c-screen", Ct)).Status);

        // The read-model write for holds is made to fail at the database (a real privilege error), as a transient outage would.
        await Exec("REVOKE INSERT ON revenant.hold_view FROM revenant_read_projector");
        try
        {
            await using var first = Worker(logins, stream, relay: true, consumer: true, "c-first");
            await first.StartAsync();
            await WaitAsync(async () => await Scalar<long>("SELECT count(*) FROM revenant.payment_view WHERE scenario_run_id = @r", ("r", run.Value)) == 1,
                "the payment event to be projected while the hold event fails");
            await WaitAsync(async () => await Scalar<long>("SELECT count(*) FROM revenant.outbox WHERE seq > @s AND status <> 'published'", ("s", baseSeq)) == 0, "the relay");
            await Task.Delay(800); // several redeliveries of the failing entry (claim idle 200 ms)
            Assert.Equal(0L, await Scalar<long>("SELECT count(*) FROM revenant.hold_view WHERE scenario_run_id = @r", ("r", run.Value)));
            Assert.Equal(0L, await HoldEventsInInboxAsync(run.Value)); // rolled back together with its effect
            using var mux = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
            Assert.True((await mux.GetDatabase().StreamGroupInfoAsync(stream)).Single().PendingMessageCount > 0); // not acknowledged, not lost
            await first.StopAsync(); // the consumer process goes away
        }
        finally
        {
            await Exec("GRANT INSERT ON revenant.hold_view TO revenant_read_projector");
        }

        await using var restarted = Worker(logins, stream, relay: false, consumer: true, "c-restarted");
        await restarted.StartAsync();
        await WaitAsync(async () => await UnconsumedAsync(baseSeq) == 0, "the restarted consumer to reclaim and apply the pending entries");
        Assert.Equal(1L, await HoldEventsInInboxAsync(run.Value));
        await AssertAgreesWithLedgerAsync(run.Value);
        Assert.Equal("L:Held", await Scalar<string>("SELECT string_agg(intent_id || ':' || status, ',') FROM revenant.payment_view WHERE scenario_run_id = @r", ("r", run.Value)));
        Assert.Equal("Active", await Scalar<string>("SELECT status FROM revenant.hold_view WHERE scenario_run_id = @r", ("r", run.Value)));
        await restarted.StopAsync();
    }

    [Fact]
    public async Task Out_of_order_stale_and_duplicate_events_never_move_the_read_model_backwards_and_bad_input_is_refused()
    {
        var logins = await db.ProvisionDeployedLoginsAsync();
        await using var worker = NpgsqlDataSource.Create(logins.Worker);
        var run = Guid.NewGuid();
        var (payment, hold, group) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        OutboxMessage Msg(string type, object payload, int version = 1) => new(Guid.NewGuid(), type, version, "x", DateTimeOffset.UtcNow,
            System.Text.Json.JsonSerializer.Serialize(payload), "corr");
        object Pay(string to, long version) => new { runId = run, paymentId = payment, intentId = "I", fromStatus = (string?)null, toStatus = to, version, factSequence = (long?)null };

        await ApplyAsync(worker, Msg(PaymentLedgerStore.EventPaymentTransitioned, Pay("Settled", 5)));
        await ApplyAsync(worker, Msg(PaymentLedgerStore.EventPaymentTransitioned, Pay("Held", 3))); // late
        await ApplyAsync(worker, Msg(PaymentLedgerStore.EventPaymentTransitioned, Pay("Cleared", 5))); // same version, different event
        Assert.Equal(("Settled", 5L), await PaymentViewAsync(payment));
        await ApplyAsync(worker, Msg(PaymentLedgerStore.EventPaymentTransitioned, Pay("Returned", 6)));
        Assert.Equal(("Returned", 6L), await PaymentViewAsync(payment));

        await ApplyAsync(worker, Msg(PaymentLedgerStore.EventHoldResolved, new { runId = run, holdId = hold, paymentId = payment, status = "Consumed", amountMinor = 10L }));
        await ApplyAsync(worker, Msg(PaymentLedgerStore.EventHoldReserved, new { runId = run, holdId = hold, paymentId = payment, status = "Active", amountMinor = 10L })); // late
        await ApplyAsync(worker, Msg(PaymentLedgerStore.EventHoldResolved, new { runId = run, holdId = hold, paymentId = payment, status = "Released", amountMinor = 10L })); // second terminal
        Assert.Equal("Consumed", await Scalar<string>("SELECT status FROM revenant.hold_view WHERE hold_id = @h", ("h", hold)));

        var posting = new { runId = run, paymentId = payment, groupId = group, purpose = "Settlement", currency = "USD", amountMinor = 10L, compensatesGroupId = (Guid?)null };
        await ApplyAsync(worker, Msg(PaymentLedgerStore.EventLedgerPosted, posting));
        await ApplyAsync(worker, Msg(PaymentLedgerStore.EventLedgerPosted, new { posting.runId, posting.paymentId, posting.groupId, purpose = "Return", posting.currency, amountMinor = 99L, compensatesGroupId = (Guid?)Guid.NewGuid() }));
        Assert.Equal(("Settlement", 10L), (await Scalar<string>("SELECT purpose FROM revenant.posting_view WHERE group_id = @g", ("g", group)),
            await Scalar<long>("SELECT amount_minor FROM revenant.posting_view WHERE group_id = @g", ("g", group))));

        // Event types the projection does not own are ignored; an unsupported version or a malformed payload is refused (and would
        // be retried and dead-lettered by the consumer, never silently dropped).
        await ApplyAsync(worker, Msg(PaymentLedgerStore.EventCaseOpened, new { anything = 1 }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyAsync(worker, Msg(PaymentLedgerStore.EventPaymentTransitioned, Pay("Held", 9), version: 2)));
        await Assert.ThrowsAnyAsync<Exception>(() => ApplyAsync(worker, Msg(PaymentLedgerStore.EventLedgerPosted, new { runId = run })));
        Assert.Equal(("Returned", 6L), await PaymentViewAsync(payment));

        // The projection login writes only the three views and the inbox.
        foreach (var sql in new[]
        {
            "UPDATE revenant.payment SET status = 'Rejected'", "DELETE FROM revenant.outbox", "UPDATE revenant.outbox SET status = 'pending'",
            "INSERT INTO revenant.finance_fact (tenant_id) VALUES ('x')", "DELETE FROM revenant.posting_view", "UPDATE revenant.posting_view SET amount_minor = 1",
        })
        {
            await using var cmd = worker.CreateCommand(sql);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }
    }

    // ---------------------------------------------------------------------------------------------------------

    private async Task AssertAgreesWithLedgerAsync(Guid run)
    {
        // Payments: same set, same status and version as the authoritative payment row.
        Assert.Equal(0L, await Scalar<long>(
            """
            SELECT count(*) FROM (
              SELECT p.id, p.status, p.version FROM revenant.payment p WHERE p.scenario_run_id = @r
              EXCEPT SELECT v.payment_id, v.status, v.version FROM revenant.payment_view v WHERE v.scenario_run_id = @r) d
            """, ("r", run)));
        Assert.Equal(await Scalar<long>("SELECT count(*) FROM revenant.payment WHERE scenario_run_id = @r", ("r", run)),
            await Scalar<long>("SELECT count(*) FROM revenant.payment_view WHERE scenario_run_id = @r", ("r", run)));
        // Holds: same set and the ledger's hold status.
        Assert.Equal(0L, await Scalar<long>(
            """
            SELECT count(*) FROM (
              (SELECT h.id, h.status, h.amount_minor FROM revenant.hold h WHERE h.scenario_run_id = @r
               EXCEPT SELECT v.hold_id, v.status, v.amount_minor FROM revenant.hold_view v WHERE v.scenario_run_id = @r)
              UNION ALL
              (SELECT v.hold_id, v.status, v.amount_minor FROM revenant.hold_view v WHERE v.scenario_run_id = @r
               EXCEPT SELECT h.id, h.status, h.amount_minor FROM revenant.hold h WHERE h.scenario_run_id = @r)) d
            """, ("r", run)));
        // Postings: same ledger groups with the ledger's purpose, link and credit-leg total.
        Assert.Equal(0L, await Scalar<long>(
            """
            SELECT count(*) FROM (
              (SELECT g.id, g.payment_id, g.purpose, g.compensates_group_id, (SELECT sum(e.amount_minor) FROM revenant.ledger_entry e WHERE e.group_id = g.id AND e.direction = 'Credit')
                 FROM revenant.ledger_group g WHERE g.scenario_run_id = @r
               EXCEPT SELECT v.group_id, v.payment_id, v.purpose, v.compensates_group_id, v.amount_minor FROM revenant.posting_view v WHERE v.scenario_run_id = @r)
              UNION ALL
              (SELECT v.group_id, v.payment_id, v.purpose, v.compensates_group_id, v.amount_minor FROM revenant.posting_view v WHERE v.scenario_run_id = @r
               EXCEPT SELECT g.id, g.payment_id, g.purpose, g.compensates_group_id, (SELECT sum(e.amount_minor) FROM revenant.ledger_entry e WHERE e.group_id = g.id AND e.direction = 'Credit')
                 FROM revenant.ledger_group g WHERE g.scenario_run_id = @r)) d
            """, ("r", run)));
    }

    /// <summary>The expected outcomes of the journey, so agreement with the ledger is not vacuous.</summary>
    private async Task AssertJourneyOutcomesAsync(Guid run)
    {
        var statuses = await Scalar<string>(
            "SELECT string_agg(intent_id || ':' || status, ',' ORDER BY intent_id) FROM revenant.payment_view WHERE scenario_run_id = @r", ("r", run));
        Assert.Equal("H:Returned,L:Settled,N:Settled,R:Rejected", statuses);
        Assert.Equal("H:Consumed,L:Consumed,R:Released", await Scalar<string>(
            "SELECT string_agg(p.intent_id || ':' || v.status, ',' ORDER BY p.intent_id) FROM revenant.hold_view v JOIN revenant.payment p ON p.id = v.payment_id WHERE v.scenario_run_id = @r", ("r", run)));
        Assert.Equal(4L, await Scalar<long>("SELECT count(*) FROM revenant.posting_view WHERE scenario_run_id = @r", ("r", run)));
        Assert.Equal(1L, await Scalar<long>("SELECT count(*) FROM revenant.posting_view WHERE scenario_run_id = @r AND compensates_group_id IS NOT NULL", ("r", run)));
    }

    /// <summary>Marks events left pending by other tests of the shared container as out of scope and returns the sequence boundary.</summary>
    private async Task<long> BaselineAsync()
    {
        await Exec("UPDATE revenant.outbox SET status = 'published', published_at = now() - interval '1 hour' WHERE status = 'pending'");
        return await Scalar<long>("SELECT coalesce(max(seq), 0) FROM revenant.outbox");
    }

    /// <summary>Events of this test's window that the projection has not recorded in its inbox.</summary>
    private Task<long> UnconsumedAsync(long baseSeq) => Scalar<long>(
        """
        SELECT count(*) FROM revenant.outbox o
         WHERE o.seq > @s AND NOT EXISTS (SELECT 1 FROM revenant.inbox i WHERE i.consumer_id = @c AND i.event_id = o.event_id)
        """, ("s", baseSeq), ("c", RevenantEventProjection.ConsumerId));

    private Task<long> HoldEventsInInboxAsync(Guid run) => Scalar<long>(
        """
        SELECT count(*) FROM revenant.inbox i JOIN revenant.outbox o ON o.event_id = i.event_id
          JOIN revenant.hold h ON o.aggregate_id = 'hold:' || replace(h.id::text, '-', '')
         WHERE i.consumer_id = @c AND h.scenario_run_id = @r
        """, ("c", RevenantEventProjection.ConsumerId), ("r", run));

    private Task<long> ViewRowsAsync(Guid run) => Scalar<long>(
        """
        SELECT (SELECT count(*) FROM revenant.payment_view WHERE scenario_run_id = @r)
             + (SELECT count(*) FROM revenant.hold_view WHERE scenario_run_id = @r)
             + (SELECT count(*) FROM revenant.posting_view WHERE scenario_run_id = @r)
        """, ("r", run));

    private Task<string> SnapshotAsync(Guid run) => Scalar<string>(
        """
        SELECT md5(coalesce((SELECT string_agg(payment_id || status || version || last_event_id || updated_utc::text, ',' ORDER BY payment_id) FROM revenant.payment_view WHERE scenario_run_id = @r), '')
              || coalesce((SELECT string_agg(hold_id || status || last_event_id || updated_utc::text, ',' ORDER BY hold_id) FROM revenant.hold_view WHERE scenario_run_id = @r), '')
              || coalesce((SELECT string_agg(group_id || purpose || amount_minor || event_id || recorded_utc::text, ',' ORDER BY group_id) FROM revenant.posting_view WHERE scenario_run_id = @r), ''))
        """, ("r", run));

    private async Task<(string Status, long Version)> PaymentViewAsync(Guid payment) =>
        (await Scalar<string>("SELECT status FROM revenant.payment_view WHERE payment_id = @p", ("p", payment)),
         await Scalar<long>("SELECT version FROM revenant.payment_view WHERE payment_id = @p", ("p", payment)));

    private async Task<(string Bank, string Account, string DestBank, string Dest)> AccountsAsync(Guid run)
    {
        await using var cmd = db.Owner.CreateCommand(
            """
            SELECT b.local_bank_id, v.local_account_id, v.available_minor FROM revenant.account_available_balance v
              JOIN revenant.bank b ON b.id = v.bank_id WHERE v.scenario_run_id = @r ORDER BY v.available_minor DESC, v.local_account_id
            """);
        cmd.Parameters.AddWithValue("r", run);
        var rows = new List<(string, string)>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            rows.Add((r.GetString(0), r.GetString(1)));
        }
        var source = rows[0];
        var dest = rows.First(x => x.Item1 != source.Item1);
        return (source.Item1, source.Item2, dest.Item1, dest.Item2);
    }

    private static async Task ApplyAsync(NpgsqlDataSource worker, OutboxMessage message)
    {
        await using var connection = await worker.OpenConnectionAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await RevenantEventProjection.ApplyAsync(message, tx, Ct);
        await tx.CommitAsync();
    }

    private async Task Exec(string sql)
    {
        await using var cmd = db.Owner.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }

    private Task<T> Scalar<T>(string sql, params (string Name, object Value)[] args) => PaymentKit.Scalar<T>(db.Owner, sql, args);

    private static ServiceProvider LedgerHost(LedgerDatabase.DeployedLogins logins, string tenant)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:RevenantLedger"] = logins.WorkerLedger,
            ["Revenant:Ledger:TenantId"] = tenant,
        }).Build();
        return new ServiceCollection().AddLogging()
            .AddRevenantLedger(config, tx => new PostgresOutboxWriter(tx, new MessagingTables("revenant")))
            .BuildServiceProvider();
    }

    /// <summary>
    /// The Worker's relay and/or read-projection consumer exactly as <c>Hosts.Worker/Program.cs</c> registers them: the relay on
    /// <c>ConnectionStrings:RevenantLedger</c> (sands_worker_ledger), the consumer on <c>ConnectionStrings:Postgres</c> (sands_worker).
    /// </summary>
    private WorkerProcess Worker(LedgerDatabase.DeployedLogins logins, string stream, bool relay, bool consumer, string name)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = logins.Worker,
            ["ConnectionStrings:Redis"] = redis.ConnectionString,
            ["ConnectionStrings:RevenantLedger"] = logins.WorkerLedger,
        };
        if (relay)
        {
            settings["Revenant:Relay:StreamKey"] = stream;
            settings["Revenant:Relay:IdleDelayMs"] = "20";
        }
        if (consumer)
        {
            settings["Revenant:ReadProjection:StreamKey"] = stream;
            settings["Revenant:ReadProjection:IdleDelayMs"] = "20";
            settings["Revenant:ReadProjection:ClaimIdleMs"] = "200";
            settings["Revenant:ReadProjection:ConsumerName"] = name;
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection().AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddMarrowDependencies(config)
            .AddMarrowOutboxRelayService(config, "Revenant:Relay", "RevenantLedger", new MessagingTables("revenant"))
            .AddMarrowInboxConsumerService(config, "Revenant:ReadProjection", new MessagingTables("revenant"),
                RevenantEventProjection.ConsumerId, RevenantEventProjection.ApplyAsync);
        return new WorkerProcess(services.BuildServiceProvider());
    }

    private sealed class WorkerProcess(ServiceProvider provider) : IAsyncDisposable
    {
        public async Task StartAsync()
        {
            foreach (var service in provider.GetServices<IHostedService>())
            {
                await service.StartAsync(Ct);
            }
        }

        public async Task StopAsync()
        {
            foreach (var service in provider.GetServices<IHostedService>())
            {
                await service.StopAsync(Ct);
            }
        }

        public ValueTask DisposeAsync() => provider.DisposeAsync();
    }

    private static async Task WaitAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            await Task.Delay(50);
        }
    }
}
