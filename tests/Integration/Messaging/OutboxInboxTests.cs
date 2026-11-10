using Integration.Tests.Support;
using Marrow.Infrastructure.Messaging;
using Marrow.Kernel.Messaging;
using Npgsql;
using StackExchange.Redis;
using Xunit;
namespace Integration.Tests.Messaging;
/// <summary>
/// R03.04/R03.05/R03.09/R03.11/R03.12: the shared outbox/inbox mechanics against real PostgreSQL and Redis Streams,
/// using test-owned module schemas (no Revenant/Ghost Lap tables). The relay and each consumer run as separate
/// "hosts" (own Postgres pool, own Redis multiplexer) inside this test process.
/// </summary>
[Collection(MessagingCollection.Name)]
[Trait("Category", "Integration")]
public sealed class OutboxInboxTests(MessagingServices services)
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(45);
    [Fact]
    public async Task Business_row_and_outbox_row_commit_and_roll_back_together()
    {
        var logs = new LogCapture();
        await using var host = await services.NewHostAsync(logs);
        var module = await services.CreateModuleAsync("atomic");
        var rolledBack = await host.PlaceOrderAsync(module, 10, commit: false);
        Assert.Equal(0, await services.CountAsync($"SELECT count(*) FROM {module.Schema}.orders"));
        Assert.Equal(0, await services.CountAsync($"SELECT count(*) FROM {module.Schema}.outbox"));
        var orderId = Guid.NewGuid();
        var committed = await host.PlaceOrderAsync(module, 20, orderId: orderId);
        Assert.Equal(1, await services.CountAsync($"SELECT count(*) FROM {module.Schema}.orders"));
        Assert.Equal("pending", await services.ScalarAsync<string>($"SELECT status FROM {module.Schema}.outbox WHERE event_id = @id", ("id", committed.EventId)));
        Assert.Equal(0, await services.CountAsync($"SELECT count(*) FROM {module.Schema}.outbox WHERE event_id = @id", ("id", rolledBack.EventId)));
        // A business failure after the enqueue in the same transaction loses both (duplicate order id), never only one.
        await using (var connection = await host.Db.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var orphan = committed with { EventId = Guid.NewGuid() };
            await new PostgresOutboxWriter(transaction, module).EnqueueAsync(orphan, CancellationToken.None);
            await using var duplicate = new NpgsqlCommand($"INSERT INTO {module.Schema}.orders (order_id, amount) VALUES (@id, 1)", connection, transaction);
            duplicate.Parameters.AddWithValue("id", orderId);
            await Assert.ThrowsAsync<PostgresException>(() => duplicate.ExecuteNonQueryAsync());
            await transaction.RollbackAsync();
            Assert.Equal(0, await services.CountAsync($"SELECT count(*) FROM {module.Schema}.outbox WHERE event_id = @id", ("id", orphan.EventId)));
            // There is no way to save the event once the business transaction has completed.
            await Assert.ThrowsAsync<InvalidOperationException>(() => new PostgresOutboxWriter(transaction, module).EnqueueAsync(orphan, CancellationToken.None));
        }
        Assert.Equal(1, await services.CountAsync($"SELECT count(*) FROM {module.Schema}.outbox"));
    }
    [Fact]
    public async Task Concurrent_relays_claim_disjoint_rows()
    {
        var logs = new LogCapture();
        await using var a = await services.NewHostAsync(logs);
        await using var b = await services.NewHostAsync(logs);
        var module = await services.CreateModuleAsync("claim");
        for (var i = 0; i < 20; i++)
        {
            await a.PlaceOrderAsync(module, i);
        }
        var options = new OutboxRelayOptions { StreamKey = $"test:{module.Schema}", BatchSize = 15 };
        var relayA = new OutboxRelay(a.Db, a.Redis, module, options, a.Log);
        var relayB = new OutboxRelay(b.Db, b.Redis, module, options, b.Log);
        var claims = await Task.WhenAll(relayA.ClaimAsync(CancellationToken.None), relayB.ClaimAsync(CancellationToken.None));
        var ids = claims.SelectMany(c => c.Select(e => e.Message.EventId)).ToList();
        Assert.Equal(20, ids.Count);
        Assert.Equal(20, ids.Distinct().Count());
        Assert.Empty(await relayA.ClaimAsync(CancellationToken.None)); // every row is leased
    }
    [Fact]
    public async Task Redis_outage_keeps_the_outbox_pending_and_cross_host_delivery_recovers_after_restart()
    {
        var logs = new LogCapture();
        await using var producer = await services.NewHostAsync(logs);
        await using var consumerHost = await services.NewHostAsync(logs);
        var producerModule = await services.CreateModuleAsync("prod");
        var consumerModule = await services.CreateModuleAsync("cons");
        var stream = $"test:{producerModule.Schema}";
        var relay = new OutboxRelay(producer.Db, producer.Redis, producerModule, new OutboxRelayOptions
        {
            StreamKey = stream,
            OutageRetry = new RetrySchedule(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200), 0.5),
        }, producer.Log);
        var consumer = new RedisStreamInboxConsumer(consumerHost.Db, consumerHost.Redis, consumerModule,
            new InboxConsumerOptions { StreamKey = stream, ConsumerId = "test-consumer", ConsumerName = "consumer-host-1" },
            TestHost.RecordEffect(consumerModule), consumerHost.Log);
        await consumer.EnsureGroupAsync();
        // A is published before the outage and not yet consumed; it must survive the Redis restart.
        var a = await producer.PlaceOrderAsync(producerModule, 5, note: "pii-jane.doe@example.test");
        Assert.Equal(1, (await relay.RunOnceAsync(CancellationToken.None)).Published);
        await services.StopRedisAsync();
        try
        {
            // The business write does not depend on Redis.
            var b = await producer.PlaceOrderAsync(producerModule, 7, note: "pii-jane.doe@example.test");
            for (var i = 0; i < 3; i++)
            {
                var pass = await relay.RunOnceAsync(CancellationToken.None);
                Assert.True(pass.TransportUnavailable);
                Assert.Equal(0, pass.Published);
                await Task.Delay(250);
            }
            Assert.Equal("pending", await StatusAsync(producerModule, b.EventId));
            Assert.Equal(0, await services.CountAsync($"SELECT attempts FROM {producerModule.Schema}.outbox WHERE event_id = @id", ("id", b.EventId)));
            var error = await services.ScalarAsync<string>($"SELECT last_error FROM {producerModule.Schema}.outbox WHERE event_id = @id", ("id", b.EventId));
            Assert.Matches("^Redis(Connection|Timeout)Exception$", error);
            var down = await Assert.ThrowsAnyAsync<Exception>(() => consumer.RunOnceAsync(CancellationToken.None));
            Assert.True(down is RedisConnectionException or TimeoutException, down.GetType().Name);
        }
        finally
        {
            await services.StartRedisAsync();
        }
        await Until(async () => (await relay.RunOnceAsync(CancellationToken.None)).Published == 1 ||
                                await services.CountAsync($"SELECT count(*) FROM {producerModule.Schema}.outbox WHERE status <> 'published'") == 0);
        Assert.Equal(0, await services.CountAsync($"SELECT count(*) FROM {producerModule.Schema}.outbox WHERE status <> 'published'"));
        var applied = 0;
        await Until(async () => (applied += (await consumer.RunOnceAsync(CancellationToken.None)).Applied) == 2);
        Assert.Equal(1, await EffectsAsync(consumerModule, a.EventId));
        Assert.Equal(12, await services.CountAsync($"SELECT sum(amount) FROM {consumerModule.Schema}.effects"));
        Assert.Equal(0, (await consumerHost.Redis.GetDatabase().StreamPendingAsync(stream, "test-consumer")).PendingMessageCount);
        // R03.09: correlation is logged, the payload (here with an e-mail address) is not.
        var text = logs.AllText();
        Assert.Contains(a.CorrelationId, text);
        Assert.Contains("outbox.transport_unavailable", text);
        Assert.DoesNotContain("jane.doe", text);
        Assert.DoesNotContain("redis-test-only-password", text);
    }
    [Fact]
    public async Task Crash_after_publication_before_confirmation_redelivers_and_the_inbox_applies_once()
    {
        var logs = new LogCapture();
        await using var relayHostA = await services.NewHostAsync(logs);
        await using var relayHostB = await services.NewHostAsync(logs);
        await using var consumerHost = await services.NewHostAsync(logs);
        var producerModule = await services.CreateModuleAsync("prod");
        var consumerModule = await services.CreateModuleAsync("cons");
        var stream = $"test:{producerModule.Schema}";
        var options = new OutboxRelayOptions { StreamKey = stream, LeaseDuration = TimeSpan.FromMilliseconds(500) };
        var consumer = new RedisStreamInboxConsumer(consumerHost.Db, consumerHost.Redis, consumerModule,
            new InboxConsumerOptions { StreamKey = stream, ConsumerId = "test-consumer", ConsumerName = "consumer-host-1" },
            TestHost.RecordEffect(consumerModule), consumerHost.Log);
        await consumer.EnsureGroupAsync();
        var message = await relayHostA.PlaceOrderAsync(producerModule, 42);
        // Relay A claims and publishes, then "crashes" before its confirming update.
        var relayA = new OutboxRelay(relayHostA.Db, relayHostA.Redis, producerModule, options, relayHostA.Log);
        var claimed = Assert.Single(await relayA.ClaimAsync(CancellationToken.None));
        var firstEntry = await relayA.PublishAsync(claimed);
        Assert.Equal("pending", await StatusAsync(producerModule, message.EventId));
        // Relay B cannot take the row while A's lease holds, then republishes it once the lease lapses.
        var relayB = new OutboxRelay(relayHostB.Db, relayHostB.Redis, producerModule, options, relayHostB.Log);
        Assert.Equal(0, (await relayB.RunOnceAsync(CancellationToken.None)).Claimed);
        await Until(async () => (await relayB.RunOnceAsync(CancellationToken.None)).Published == 1);
        Assert.Equal("published", await StatusAsync(producerModule, message.EventId));
        Assert.Equal(2, await consumerHost.Redis.GetDatabase().StreamLengthAsync(stream));
        // A's late confirmation is refused: it no longer holds the lease.
        Assert.False(await relayA.ConfirmAsync(claimed, firstEntry));
        var pass = await consumer.RunOnceAsync(CancellationToken.None);
        Assert.Equal((2, 1, 1), (pass.Read, pass.Applied, pass.Duplicates));
        Assert.Equal(1, await EffectsAsync(consumerModule, message.EventId));
        Assert.Equal(1, await services.CountAsync($"SELECT count(*) FROM {consumerModule.Schema}.inbox WHERE consumer_id = 'test-consumer'"));
        Assert.Equal(0, (await consumerHost.Redis.GetDatabase().StreamPendingAsync(stream, "test-consumer")).PendingMessageCount);
        Assert.Contains("outbox.lease_lost", logs.AllText());
    }
    [Fact]
    public async Task Crash_after_apply_and_concurrent_duplicate_apply_have_one_business_effect()
    {
        var logs = new LogCapture();
        await using var producer = await services.NewHostAsync(logs);
        await using var hostA = await services.NewHostAsync(logs);
        await using var hostB = await services.NewHostAsync(logs);
        var producerModule = await services.CreateModuleAsync("prod");
        var consumerModule = await services.CreateModuleAsync("cons");
        var stream = $"test:{producerModule.Schema}";
        InboxConsumerOptions Options(string name) => new()
        {
            StreamKey = stream, ConsumerId = "test-consumer", ConsumerName = name, ClaimIdleTime = TimeSpan.FromMilliseconds(200),
        };
        var consumerA = new RedisStreamInboxConsumer(hostA.Db, hostA.Redis, consumerModule, Options("host-a"), TestHost.RecordEffect(consumerModule), hostA.Log);
        var consumerB = new RedisStreamInboxConsumer(hostB.Db, hostB.Redis, consumerModule, Options("host-b"), TestHost.RecordEffect(consumerModule), hostB.Log);
        await consumerA.EnsureGroupAsync();
        await consumerB.EnsureGroupAsync(); // second creation is a no-op
        var relay = new OutboxRelay(producer.Db, producer.Redis, producerModule, new OutboxRelayOptions { StreamKey = stream }, producer.Log);
        var message = await producer.PlaceOrderAsync(producerModule, 9);
        Assert.Equal(1, (await relay.RunOnceAsync(CancellationToken.None)).Published);
        // Host A reads and commits the effect, then "crashes" before acknowledging the stream entry.
        var (_, fresh) = await consumerA.ReadAsync();
        var entry = Assert.Single(fresh);
        Assert.Equal(InboxApplyOutcome.Applied, await consumerA.ApplyAsync(StreamEntryCodec.TryDecode(entry.Values)!, CancellationToken.None));
        Assert.Equal(1, (await hostA.Redis.GetDatabase().StreamPendingAsync(stream, "test-consumer")).PendingMessageCount);
        // Host B reclaims the idle entry, finds the inbox row, applies nothing and acknowledges.
        var recovered = new ConsumerPass(0, 0, 0, 0, 0);
        await Until(async () => (recovered = await consumerB.RunOnceAsync(CancellationToken.None)).Read == 1);
        Assert.Equal((0, 1), (recovered.Applied, recovered.Duplicates));
        Assert.Equal(0, (await hostB.Redis.GetDatabase().StreamPendingAsync(stream, "test-consumer")).PendingMessageCount);
        Assert.Equal(1, await EffectsAsync(consumerModule, message.EventId));
        // Two hosts applying the same new event at the same moment: the inbox key lets exactly one effect commit.
        var second = await producer.PlaceOrderAsync(producerModule, 3);
        var decoded = new OutboxMessage(second.EventId, second.EventType, second.EventVersion, second.AggregateId, second.OccurredUtc,
            second.Payload, second.CorrelationId);
        var outcomes = await Task.WhenAll(
            consumerA.ApplyAsync(decoded, CancellationToken.None),
            consumerB.ApplyAsync(decoded, CancellationToken.None));
        Assert.Equal(new[] { InboxApplyOutcome.Applied, InboxApplyOutcome.Duplicate }, outcomes.Order().ToArray());
        Assert.Equal(1, await EffectsAsync(consumerModule, second.EventId));
    }
    [Fact]
    public async Task Failed_apply_rolls_back_is_redelivered_and_a_persistent_failure_is_dead_lettered()
    {
        var logs = new LogCapture();
        await using var producer = await services.NewHostAsync(logs);
        await using var consumerHost = await services.NewHostAsync(logs);
        var producerModule = await services.CreateModuleAsync("prod");
        var consumerModule = await services.CreateModuleAsync("cons");
        var stream = $"test:{producerModule.Schema}";
        var failuresLeft = new Dictionary<string, int> { ["flaky"] = 1, ["broken"] = int.MaxValue };
        var record = TestHost.RecordEffect(consumerModule);
        InboxEffect effect = async (message, transaction, ct) =>
        {
            await record(message, transaction, ct); // written, then rolled back by the throw below
            foreach (var key in failuresLeft.Keys.Where(k => message.Payload.Contains($"\"note\":\"{k}\"", StringComparison.Ordinal)))
            {
                if (failuresLeft[key]-- > 0)
                {
                    throw new InvalidOperationException($"handler failure for secret-{key}");
                }
            }
        };
        var consumer = new RedisStreamInboxConsumer(consumerHost.Db, consumerHost.Redis, consumerModule, new InboxConsumerOptions
        {
            StreamKey = stream, ConsumerId = "test-consumer", ConsumerName = "consumer-host-1",
            ClaimIdleTime = TimeSpan.FromMilliseconds(50), MaxDeliveries = 3,
        }, effect, consumerHost.Log);
        await consumer.EnsureGroupAsync();
        var relay = new OutboxRelay(producer.Db, producer.Redis, producerModule, new OutboxRelayOptions { StreamKey = stream }, producer.Log);
        var flaky = await producer.PlaceOrderAsync(producerModule, 1, note: "flaky");
        var broken = await producer.PlaceOrderAsync(producerModule, 2, note: "broken");
        Assert.Equal(2, (await relay.RunOnceAsync(CancellationToken.None)).Published);
        int applied = 0, failed = 0, dead = 0;
        await Until(async () =>
        {
            await Task.Delay(80);
            var pass = await consumer.RunOnceAsync(CancellationToken.None);
            (applied, failed, dead) = (applied + pass.Applied, failed + pass.Failed, dead + pass.DeadLettered);
            return dead == 1;
        });
        Assert.Equal((1, 1 + 3), (applied, failed)); // flaky: 1 failure then applied; broken: 3 deliveries failed
        Assert.Equal(1, await EffectsAsync(consumerModule, flaky.EventId));
        Assert.Equal(0, await EffectsAsync(consumerModule, broken.EventId));
        Assert.Equal(0, await services.CountAsync($"SELECT count(*) FROM {consumerModule.Schema}.inbox WHERE event_id = @id", ("id", broken.EventId)));
        var redis = consumerHost.Redis.GetDatabase();
        Assert.Equal(0, (await redis.StreamPendingAsync(stream, "test-consumer")).PendingMessageCount);
        var deadEntry = Assert.Single(await redis.StreamRangeAsync($"{stream}:dead:test-consumer"));
        Assert.Equal(broken.EventId.ToString("D"), deadEntry["event_id"].ToString());
        Assert.Equal("max_deliveries", deadEntry["dead_reason"].ToString());
        var text = logs.AllText();
        Assert.Contains("inbox.dead_lettered", text);
        Assert.Contains($"correlation={broken.CorrelationId}", text);
        Assert.Contains("error=InvalidOperationException", text);
        Assert.DoesNotContain("secret-", text); // the exception message is not logged
    }
    [Fact]
    public async Task Publish_failures_are_retried_with_bounds_and_poisoned_rows_stay_visible()
    {
        var logs = new LogCapture();
        await using var producer = await services.NewHostAsync(logs);
        var module = await services.CreateModuleAsync("poison");
        var stream = $"test:{module.Schema}";
        await producer.Redis.GetDatabase().StringSetAsync(stream, "not-a-stream"); // every XADD now fails with WRONGTYPE
        var relay = new OutboxRelay(producer.Db, producer.Redis, module, new OutboxRelayOptions
        {
            StreamKey = stream, MaxAttempts = 3,
            Retry = new RetrySchedule(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(40), 0.5),
        }, producer.Log);
        var message = await producer.PlaceOrderAsync(module, 1, note: "secret-payload-value");
        var passes = new List<RelayPass>();
        await Until(async () =>
        {
            await Task.Delay(30);
            var pass = await relay.RunOnceAsync(CancellationToken.None);
            passes.Add(pass);
            return pass.Poisoned == 1;
        });
        Assert.Equal(2, passes.Sum(p => p.Retried));
        Assert.Equal("poisoned", await StatusAsync(module, message.EventId));
        var poisoned = Assert.Single(await relay.ListPoisonedAsync(10, CancellationToken.None));
        Assert.Equal((message.EventId, message.CorrelationId, 3, "RedisServerException:WRONGTYPE"),
            (poisoned.EventId, poisoned.CorrelationId, poisoned.Attempts, poisoned.LastError));
        Assert.Equal(1, await services.CountAsync($"SELECT count(*) FROM {module.Schema}.orders")); // business row untouched
        Assert.Equal(0, (await relay.RunOnceAsync(CancellationToken.None)).Claimed); // no longer retried
        var text = logs.AllText();
        Assert.Contains($"outbox.poisoned event={message.EventId}", text);
        Assert.Equal(2, logs.Lines.Count(l => l.EventId == 3103));
        Assert.DoesNotContain("secret-payload-value", text);
    }
    /// <summary>
    /// The supported failure model's hardest Redis case: the process is killed right after acknowledging publication
    /// (PostgreSQL already says <c>published</c>) and after a consumer read the entries without acknowledging them.
    /// With the Compose AOF settings both the entries and the consumer group's pending list survive, and the inbox
    /// keeps the business effect single for the entry that was applied before the crash.
    /// </summary>
    [Fact]
    public async Task Redis_killed_after_acknowledging_publication_keeps_entries_and_pending_deliveries()
    {
        var logs = new LogCapture();
        await using var producer = await services.NewHostAsync(logs);
        await using var consumerHost = await services.NewHostAsync(logs);
        var producerModule = await services.CreateModuleAsync("kprod");
        var consumerModule = await services.CreateModuleAsync("kcons");
        var stream = $"test:{producerModule.Schema}";
        var relay = new OutboxRelay(producer.Db, producer.Redis, producerModule, new OutboxRelayOptions { StreamKey = stream }, producer.Log);
        var consumer = new RedisStreamInboxConsumer(consumerHost.Db, consumerHost.Redis, consumerModule,
            new InboxConsumerOptions { StreamKey = stream, ConsumerId = "kill-consumer", ConsumerName = "after-restart", ClaimIdleTime = TimeSpan.FromMilliseconds(200) },
            TestHost.RecordEffect(consumerModule), consumerHost.Log);
        await consumer.EnsureGroupAsync();
        var a = await producer.PlaceOrderAsync(producerModule, 3);
        var b = await producer.PlaceOrderAsync(producerModule, 4);
        Assert.Equal(2, (await relay.RunOnceAsync(CancellationToken.None)).Published);
        Assert.Equal(0, await services.CountAsync($"SELECT count(*) FROM {producerModule.Schema}.outbox WHERE status <> 'published'"));

        // A consumer instance reads both, commits the effect of the first, then dies before any acknowledgement.
        var delivered = await consumerHost.Redis.GetDatabase().StreamReadGroupAsync(stream, "kill-consumer", "crashed-instance", StreamPosition.NewMessages, 10);
        Assert.Equal(2, delivered.Length);
        Assert.Equal(InboxApplyOutcome.Applied, await consumer.ApplyAsync(StreamEntryCodec.TryDecode(delivered[0].Values)!, CancellationToken.None));

        await services.KillRedisAsync();
        await services.StartRedisAsync();

        long pending = -1, length = -1;
        var back = DateTime.UtcNow + Deadline;
        while (true)
        {
            try
            {
                var redis = consumerHost.Redis.GetDatabase();
                pending = (await redis.StreamPendingAsync(stream, "kill-consumer")).PendingMessageCount;
                length = await redis.StreamLengthAsync(stream);
                break;
            }
            catch (RedisException) when (DateTime.UtcNow < back)
            {
                await Task.Delay(100); // reconnecting, or LOADING while Redis replays its append-only file
            }
        }
        Assert.Equal(2, length);   // both acknowledged XADDs survived the kill
        Assert.Equal(2, pending);  // and so did the unacknowledged deliveries
        await Task.Delay(300);     // older than ClaimIdleTime: reclaimable
        int applied = 0, duplicates = 0;
        await Until(async () =>
        {
            var pass = await consumer.RunOnceAsync(CancellationToken.None);
            applied += pass.Applied;
            duplicates += pass.Duplicates;
            return applied + duplicates == 2;
        });
        Assert.Equal((1, 1), (applied, duplicates));
        Assert.Equal(1, await EffectsAsync(consumerModule, a.EventId));
        Assert.Equal(1, await EffectsAsync(consumerModule, b.EventId));
        Assert.Equal(0, (await consumerHost.Redis.GetDatabase().StreamPendingAsync(stream, "kill-consumer")).PendingMessageCount);
    }

    /// <summary>
    /// Server-wide refusals (here an ACL that forbids XADD; the same class as LOADING after a restart, MISCONF on a full
    /// AOF disk, OOM) are outages: the row stays pending with zero attempts however long they last, and is published
    /// once Redis accepts writes again. Only message-specific errors (WRONGTYPE ...) count toward poisoning.
    /// </summary>
    [Fact]
    public async Task Server_wide_redis_refusals_never_poison_pending_rows()
    {
        var logs = new LogCapture();
        await using var producer = await services.NewHostAsync(logs);
        var module = await services.CreateModuleAsync("refuse");
        var stream = $"test:{module.Schema}";
        var relay = new OutboxRelay(producer.Db, producer.Redis, module, new OutboxRelayOptions
        {
            StreamKey = stream, MaxAttempts = 2,
            OutageRetry = new RetrySchedule(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(40), 0.5),
        }, producer.Log);
        var message = await producer.PlaceOrderAsync(module, 9);
        Assert.Equal("OK", await services.RedisCliAsync("ACL", "SETUSER", "default", "-xadd"));
        try
        {
            var outages = 0;
            for (var i = 0; i < 10; i++)
            {
                var pass = await relay.RunOnceAsync(CancellationToken.None);
                Assert.Equal(0, pass.Poisoned);
                Assert.Equal(0, pass.Retried);
                outages += pass.TransportUnavailable ? 1 : 0;
                await Task.Delay(60);
            }
            Assert.True(outages >= 5, $"only {outages} outage passes");
            Assert.Equal("pending", await StatusAsync(module, message.EventId));
            Assert.Equal(0, await services.CountAsync($"SELECT attempts FROM {module.Schema}.outbox WHERE event_id = @id", ("id", message.EventId)));
            Assert.Equal("RedisServerException:NOPERM", await services.ScalarAsync<string>($"SELECT last_error FROM {module.Schema}.outbox WHERE event_id = @id", ("id", message.EventId)));
        }
        finally
        {
            await services.RedisCliAsync("ACL", "SETUSER", "default", "+xadd");
        }
        await Until(async () => (await relay.RunOnceAsync(CancellationToken.None)).Published == 1);
        Assert.Equal("published", await StatusAsync(module, message.EventId));
    }

    private Task<string> StatusAsync(MessagingTables module, Guid eventId) =>
        services.ScalarAsync<string>($"SELECT status FROM {module.Schema}.outbox WHERE event_id = @id", ("id", eventId));
    private Task<long> EffectsAsync(MessagingTables module, Guid eventId) =>
        services.CountAsync($"SELECT count(*) FROM {module.Schema}.effects WHERE event_id = @id", ("id", eventId));
    private static async Task Until(Func<Task<bool>> condition)
    {
        var stop = DateTime.UtcNow + Deadline;
        while (true)
        {
            try
            {
                if (await condition())
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is RedisConnectionException or TimeoutException && DateTime.UtcNow < stop)
            {
                // Redis is still reconnecting after a restart.
            }
            if (DateTime.UtcNow >= stop)
            {
                throw new TimeoutException("Condition not reached within the deadline.");
            }
            await Task.Delay(100);
        }
    }
}
