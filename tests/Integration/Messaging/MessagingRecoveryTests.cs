using System.Diagnostics;
using Integration.Tests.Support;
using Marrow.Infrastructure.Messaging;
using StackExchange.Redis;
using Xunit;
using Xunit.Abstractions;

namespace Integration.Tests.Messaging;

/// <summary>
/// Spec 07 task 7.5: recovery handoffs of the shared outbox/inbox/relay mechanics against real PostgreSQL and Redis (Compose
/// settings: append-only file with <c>appendfsync always</c>). Safe trimming keeps every pending or unread entry, retention is
/// bounded, poison and dead-letter requeue go back through inbox deduplication, the Redis-volume-loss procedure re-publishes from
/// PostgreSQL, and the relay's throughput under <c>appendfsync always</c> is measured (a single local measurement, not a claim).
/// **Validates: Requirements R07.09**
/// </summary>
[Collection(MessagingCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MessagingRecoveryTests(MessagingServices services, ITestOutputHelper output)
{
    [Fact]
    public async Task Trimming_never_removes_an_entry_some_group_has_not_read_or_acknowledged_and_respects_retention()
    {
        var logs = new LogCapture();
        await using var host = await services.NewHostAsync(logs);
        var module = await services.CreateModuleAsync("trim");
        var stream = $"test:{module.Schema}";
        var relay = new OutboxRelay(host.Db, host.Redis, module, new OutboxRelayOptions { StreamKey = stream }, host.Log);
        for (var i = 0; i < 10; i++)
        {
            await host.PlaceOrderAsync(module, i);
        }
        Assert.Equal(10, (await relay.RunOnceAsync(CancellationToken.None)).Published);
        var redis = host.Redis.GetDatabase();
        var now = DateTimeOffset.UtcNow.AddMinutes(1);

        // No consumer group: nobody has read anything, so nothing is trimmed even with a zero bound.
        var none = await RedisStreamMaintenance.TrimAsync(redis, stream, 0, TimeSpan.Zero, now);
        Assert.Equal((0L, 10L, true), (none.Removed, none.Length, none.OverBound));

        // Group "fast" reads and acknowledges everything; group "slow" has 3 delivered-but-unacknowledged entries and 7 unread.
        await redis.StreamCreateConsumerGroupAsync(stream, "fast", StreamPosition.Beginning);
        await redis.StreamCreateConsumerGroupAsync(stream, "slow", StreamPosition.Beginning);
        var all = await redis.StreamReadGroupAsync(stream, "fast", "f1", StreamPosition.NewMessages, 10);
        await redis.StreamAcknowledgeAsync(stream, "fast", [.. all.Select(e => e.Id)]);
        var slow = await redis.StreamReadGroupAsync(stream, "slow", "s1", StreamPosition.NewMessages, 3);
        Assert.Equal(3, slow.Length);
        var pendingOnly = await RedisStreamMaintenance.TrimAsync(redis, stream, 0, TimeSpan.Zero, now);
        Assert.Equal((0L, 10L, true), (pendingOnly.Removed, pendingOnly.Length, pendingOnly.OverBound));

        // Once "slow" acknowledges its 3, exactly those 3 go; its 7 unread entries stay although the bound says 0.
        await redis.StreamAcknowledgeAsync(stream, "slow", [.. slow.Select(e => e.Id)]);
        var acked = await RedisStreamMaintenance.TrimAsync(redis, stream, 0, TimeSpan.Zero, now);
        Assert.Equal((3L, 7L, true), (acked.Removed, acked.Length, acked.OverBound));
        Assert.Equal(all[3].Id.ToString(), (await redis.StreamRangeAsync(stream, "-", "+", 1))[0].Id.ToString());

        // Everything read and acknowledged by both groups: the bound applies (keep newest 2) unless retention protects younger entries.
        var rest = await redis.StreamReadGroupAsync(stream, "slow", "s1", StreamPosition.NewMessages, 10);
        await redis.StreamAcknowledgeAsync(stream, "slow", [.. rest.Select(e => e.Id)]);
        var retained = await RedisStreamMaintenance.TrimAsync(redis, stream, 2, TimeSpan.FromHours(1), now);
        Assert.Equal((0L, 7L), (retained.Removed, retained.Length));
        var bounded = await RedisStreamMaintenance.TrimAsync(redis, stream, 2, TimeSpan.Zero, now);
        Assert.Equal((5L, 2L, false), (bounded.Removed, bounded.Length, bounded.OverBound));
        // PostgreSQL still holds all 10 published rows: trimming the transport never deletes the source of truth.
        Assert.Equal(10, await services.CountAsync($"SELECT count(*) FROM {module.Schema}.outbox WHERE status = 'published'"));
    }

    [Fact]
    public async Task Dead_lettered_and_poisoned_messages_requeue_through_deduplication_with_one_effect()
    {
        var logs = new LogCapture();
        await using var producer = await services.NewHostAsync(logs);
        await using var consumerHost = await services.NewHostAsync(logs);
        var producerModule = await services.CreateModuleAsync("dlqp");
        var consumerModule = await services.CreateModuleAsync("dlqc");
        var stream = $"test:{producerModule.Schema}";
        var broken = true;
        var record = TestHost.RecordEffect(consumerModule);
        InboxEffect effect = async (message, tx, ct) =>
        {
            if (broken)
            {
                throw new InvalidOperationException("handler down");
            }
            await record(message, tx, ct);
        };
        var consumer = new RedisStreamInboxConsumer(consumerHost.Db, consumerHost.Redis, consumerModule, new InboxConsumerOptions
        {
            StreamKey = stream, ConsumerId = "dlq-consumer", ConsumerName = "c1", ClaimIdleTime = TimeSpan.FromMilliseconds(30), MaxDeliveries = 1,
        }, effect, consumerHost.Log);
        await consumer.EnsureGroupAsync();
        var relay = new OutboxRelay(producer.Db, producer.Redis, producerModule, new OutboxRelayOptions { StreamKey = stream }, producer.Log);
        var message = await producer.PlaceOrderAsync(producerModule, 7);
        Assert.Equal(1, (await relay.RunOnceAsync(CancellationToken.None)).Published);
        var dead = 0;
        for (var i = 0; i < 40 && dead == 0; i++)
        {
            await Task.Delay(40);
            dead += (await consumer.RunOnceAsync(CancellationToken.None)).DeadLettered;
        }
        Assert.Equal(1, dead);
        var redis = consumerHost.Redis.GetDatabase();
        var deadKey = $"{stream}:dead:dlq-consumer";

        // The handler is fixed; the operator requeues. The same event id goes back on the stream and is applied once.
        broken = false;
        Assert.Equal(1, await RedisStreamMaintenance.RequeueDeadLettersAsync(redis, deadKey, stream, 10));
        Assert.Equal(0, await redis.StreamLengthAsync(deadKey));
        Assert.Equal(1, (await consumer.RunOnceAsync(CancellationToken.None)).Applied);
        // A crash between re-add and delete would re-add it once more: that copy is a duplicate, not a second effect.
        var copy = (await redis.StreamRangeAsync(stream, "-", "+")).Last();
        await redis.StreamAddAsync(stream, copy.Values);
        Assert.Equal(1, (await consumer.RunOnceAsync(CancellationToken.None)).Duplicates);
        Assert.Equal(1, await services.CountAsync($"SELECT count(*) FROM {consumerModule.Schema}.effects WHERE event_id = @id", ("id", message.EventId)));

        // A poisoned outbox row (Redis refused it MaxAttempts times) is requeued by the operator and published once Redis accepts it.
        var poisonStream = $"{stream}:poison";
        await producer.Redis.GetDatabase().StringSetAsync(poisonStream, "not-a-stream");
        var poisonRelay = new OutboxRelay(producer.Db, producer.Redis, producerModule, new OutboxRelayOptions { StreamKey = poisonStream, MaxAttempts = 1 }, producer.Log);
        var poisoned = await producer.PlaceOrderAsync(producerModule, 8);
        Assert.Equal(1, (await poisonRelay.RunOnceAsync(CancellationToken.None)).Poisoned);
        await producer.Redis.GetDatabase().KeyDeleteAsync(poisonStream);
        Assert.Equal(1, await OutboxRecovery.RequeuePoisonedAsync(producer.Db, producerModule, [poisoned.EventId, message.EventId], CancellationToken.None));
        Assert.Equal(1, (await poisonRelay.RunOnceAsync(CancellationToken.None)).Published);
        Assert.Equal("published", await services.ScalarAsync<string>($"SELECT status FROM {producerModule.Schema}.outbox WHERE event_id = @id", ("id", poisoned.EventId)));
    }

    [Fact]
    public async Task After_redis_volume_loss_the_operator_republishes_from_postgres_and_consumers_deduplicate()
    {
        var logs = new LogCapture();
        await using var producer = await services.NewHostAsync(logs);
        await using var consumerHost = await services.NewHostAsync(logs);
        var producerModule = await services.CreateModuleAsync("lossp");
        var consumerModule = await services.CreateModuleAsync("lossc");
        var stream = $"test:{producerModule.Schema}";
        var consumer = new RedisStreamInboxConsumer(consumerHost.Db, consumerHost.Redis, consumerModule, new InboxConsumerOptions
        {
            StreamKey = stream, ConsumerId = "loss-consumer", ConsumerName = "c1",
        }, TestHost.RecordEffect(consumerModule), consumerHost.Log);
        await consumer.EnsureGroupAsync();
        var relay = new OutboxRelay(producer.Db, producer.Redis, producerModule, new OutboxRelayOptions { StreamKey = stream }, producer.Log);
        var before = DateTimeOffset.UtcNow.AddSeconds(-5);
        for (var i = 0; i < 5; i++)
        {
            await producer.PlaceOrderAsync(producerModule, i);
        }
        Assert.Equal(5, (await relay.RunOnceAsync(CancellationToken.None)).Published);
        Assert.Equal(5, (await consumer.RunOnceAsync(CancellationToken.None)).Applied);
        // A second consumer had not read them yet when the Redis volume is lost (stream and groups gone).
        var lateModule = await services.CreateModuleAsync("lossl");
        await producer.Redis.GetDatabase().KeyDeleteAsync(stream);
        Assert.False(await producer.Redis.GetDatabase().KeyExistsAsync(stream));
        var late = new RedisStreamInboxConsumer(consumerHost.Db, consumerHost.Redis, lateModule, new InboxConsumerOptions
        {
            StreamKey = stream, ConsumerId = "late-consumer", ConsumerName = "c1",
        }, TestHost.RecordEffect(lateModule), consumerHost.Log);

        // Operator procedure: re-publish from PostgreSQL every row published since a point before the loss.
        Assert.Equal(5, await OutboxRecovery.RepublishSinceAsync(producer.Db, producerModule, before, CancellationToken.None));
        await consumer.EnsureGroupAsync();
        await late.EnsureGroupAsync();
        Assert.Equal(5, (await relay.RunOnceAsync(CancellationToken.None)).Published);
        var again = await consumer.RunOnceAsync(CancellationToken.None);
        Assert.Equal((0, 5), (again.Applied, again.Duplicates));
        Assert.Equal(5, (await late.RunOnceAsync(CancellationToken.None)).Applied);
        Assert.Equal(5, await services.CountAsync($"SELECT count(*) FROM {consumerModule.Schema}.effects"));
        Assert.Equal(5, await services.CountAsync($"SELECT count(*) FROM {lateModule.Schema}.effects"));
    }

    [Fact]
    public async Task Relay_throughput_with_appendfsync_always_is_measured()
    {
        Assert.Equal("appendfsync\nalways", await services.RedisCliAsync("CONFIG", "GET", "appendfsync"));
        var logs = new LogCapture();
        await using var host = await services.NewHostAsync(logs);
        var module = await services.CreateModuleAsync("tput");
        const int count = 1000;
        for (var i = 0; i < count; i++)
        {
            await host.PlaceOrderAsync(module, i);
        }
        var relay = new OutboxRelay(host.Db, host.Redis, module, new OutboxRelayOptions { StreamKey = $"test:{module.Schema}", BatchSize = 100 }, host.Log);
        var watch = Stopwatch.StartNew();
        var published = 0;
        while (published < count)
        {
            published += (await relay.RunOnceAsync(CancellationToken.None)).Published;
        }
        watch.Stop();
        var xaddWatch = Stopwatch.StartNew();
        var raw = host.Redis.GetDatabase();
        for (var i = 0; i < count; i++)
        {
            await raw.StreamAddAsync($"test:{module.Schema}:raw", "k", i);
        }
        xaddWatch.Stop();
        output.WriteLine($"MEASURED relay (claim+XADD+confirm, batch 100): {count} messages in {watch.ElapsedMilliseconds} ms = {count * 1000.0 / watch.ElapsedMilliseconds:F0} msg/s; " +
            $"sequential awaited XADD only: {count} in {xaddWatch.ElapsedMilliseconds} ms = {count * 1000.0 / xaddWatch.ElapsedMilliseconds:F0} ops/s (appendfsync always, local Docker, single run)");
        Assert.Equal(count, await services.CountAsync($"SELECT count(*) FROM {module.Schema}.outbox WHERE status = 'published'"));
        Assert.Equal(count, await raw.StreamLengthAsync($"test:{module.Schema}"));
    }
}
