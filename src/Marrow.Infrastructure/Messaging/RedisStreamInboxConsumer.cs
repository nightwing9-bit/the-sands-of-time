using Marrow.Kernel.Messaging;
using Npgsql;
using StackExchange.Redis;
namespace Marrow.Infrastructure.Messaging;
/// <summary>
/// The consumer's business effect. It runs inside the transaction that has just recorded the inbox row and must
/// write only through <paramref name="transaction"/>; throwing rolls back both.
/// </summary>
public delegate Task InboxEffect(OutboxMessage message, NpgsqlTransaction transaction, CancellationToken ct);
public sealed record InboxConsumerOptions
{
    public required string StreamKey { get; init; }
    /// <summary>Stable logical consumer: the Redis consumer group name and the inbox <c>consumer_id</c>.</summary>
    public required string ConsumerId { get; init; }
    /// <summary>This process instance within the group (for example host name plus process id).</summary>
    public required string ConsumerName { get; init; }
    public int BatchSize { get; init; } = 50;
    /// <summary>Entries delivered but unacknowledged for this long (a crashed or stuck consumer) are claimed again.</summary>
    public TimeSpan ClaimIdleTime { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Deliveries after which a failing entry is moved to <see cref="DeadLetterStreamKey"/> instead of retried.</summary>
    public int MaxDeliveries { get; init; } = 10;
    public string? DeadLetterStreamKey { get; init; }
    internal string DeadLetterKey => DeadLetterStreamKey ?? $"{StreamKey}:dead:{ConsumerId}";
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(StreamKey) || string.IsNullOrWhiteSpace(ConsumerId) || ConsumerId.Length > 200
            || string.IsNullOrWhiteSpace(ConsumerName) || BatchSize is < 1 or > 500 || MaxDeliveries is < 1 or > 1000
            || ClaimIdleTime < TimeSpan.FromMilliseconds(10))
        {
            throw new ArgumentException("Invalid inbox consumer options (StreamKey, ConsumerId <= 200 chars and ConsumerName required, BatchSize 1-500, MaxDeliveries 1-1000).");
        }
    }
}
public enum InboxApplyOutcome
{
    Applied,
    Duplicate,
}
public sealed record ConsumerPass(int Read, int Applied, int Duplicates, int Failed, int DeadLettered);
/// <summary>
/// Receives Outbox v1 messages from a Redis Stream consumer group in another host process. Each entry is applied
/// in one PostgreSQL transaction that inserts <c>(consumer_id,event_id)</c> into the consumer's inbox and performs the
/// business effect; the stream entry is acknowledged only after that commit. A redelivered entry (relay re-publish,
/// crash before acknowledgement, or a reclaimed idle entry) finds the inbox row and has no second effect.
/// </summary>
public sealed class RedisStreamInboxConsumer
{
    private readonly NpgsqlDataSource _db;
    private readonly IConnectionMultiplexer _redis;
    private readonly MessagingTables _tables;
    private readonly InboxConsumerOptions _options;
    private readonly InboxEffect _effect;
    private readonly MessagingLog _log;
    private readonly TimeProvider _time;
    public RedisStreamInboxConsumer(NpgsqlDataSource db, IConnectionMultiplexer redis, MessagingTables tables, InboxConsumerOptions options,
        InboxEffect effect, MessagingLog log, TimeProvider? time = null)
    {
        options.Validate();
        (_db, _redis, _tables, _options, _effect, _log, _time) = (db, redis, tables, options, effect, log, time ?? TimeProvider.System);
    }
    private IDatabase Redis => _redis.GetDatabase();
    /// <summary>Creates the consumer group from the start of the stream (so entries published earlier are delivered).</summary>
    public async Task EnsureGroupAsync()
    {
        try
        {
            await Redis.StreamCreateConsumerGroupAsync(_options.StreamKey, _options.ConsumerId, StreamPosition.Beginning, createStream: true);
        }
        catch (RedisServerException ex) when (ex.Message.StartsWith("BUSYGROUP", StringComparison.Ordinal))
        {
        }
    }
    public async Task<ConsumerPass> RunOnceAsync(CancellationToken ct)
    {
        var (reclaimed, fresh) = await ReadAsync();
        int applied = 0, duplicates = 0, failed = 0, dead = 0;
        foreach (var (entry, wasReclaimed) in reclaimed.Select(e => (e, true)).Concat(fresh.Select(e => (e, false))))
        {
            ct.ThrowIfCancellationRequested();
            var message = StreamEntryCodec.TryDecode(entry.Values);
            if (message is null)
            {
                await DeadLetterAsync(entry, null, "malformed");
                dead++;
                continue;
            }
            var delivery = wasReclaimed ? await DeliveryCountAsync(entry.Id) : 1;
            if (delivery > _options.MaxDeliveries)
            {
                await DeadLetterAsync(entry, message.EventId, "max_deliveries");
                dead++;
                continue;
            }
            InboxApplyOutcome outcome;
            try
            {
                outcome = await ApplyAsync(message, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Rolled back: no inbox row, no effect. The entry stays pending and is reclaimed after ClaimIdleTime.
                _log.ApplyFailed(_options.ConsumerId, message.EventId, message.CorrelationId, delivery, MessagingErrors.Sanitize(ex));
                failed++;
                continue;
            }
            await AckAsync(entry.Id);
            if (outcome == InboxApplyOutcome.Applied)
            {
                applied++;
                _log.Applied(_options.ConsumerId, message.EventId, message.CorrelationId);
            }
            else
            {
                duplicates++;
                _log.Duplicate(_options.ConsumerId, message.EventId, message.CorrelationId);
            }
        }
        return new ConsumerPass(reclaimed.Count + fresh.Count, applied, duplicates, failed, dead);
    }
    /// <summary>Polls until cancelled; transport or database errors are logged by class and retried after <paramref name="idleDelay"/>.</summary>
    public async Task RunAsync(TimeSpan idleDelay, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var idle = true;
            try
            {
                var pass = await RunOnceAsync(ct);
                idle = pass.Read == 0;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && (MessagingErrors.IsTransportUnavailable(ex) || ex is NpgsqlException or RedisServerException))
            {
                _log.ConsumerTransportUnavailable(_options.ConsumerId, MessagingErrors.Sanitize(ex));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            if (idle)
            {
                await Task.Delay(idleDelay, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }
    /// <summary>Reclaimed idle entries (from any instance of this group) first, then new entries.</summary>
    internal async Task<(IReadOnlyList<StreamEntry> Reclaimed, IReadOnlyList<StreamEntry> Fresh)> ReadAsync()
    {
        try
        {
            return await ReadCoreAsync();
        }
        catch (RedisServerException ex) when (ex.Message.StartsWith("NOGROUP", StringComparison.Ordinal))
        {
            await EnsureGroupAsync();
            return await ReadCoreAsync();
        }
    }
    private async Task<(IReadOnlyList<StreamEntry>, IReadOnlyList<StreamEntry>)> ReadCoreAsync()
    {
        var claim = await Redis.StreamAutoClaimAsync(_options.StreamKey, _options.ConsumerId, _options.ConsumerName,
            (long)_options.ClaimIdleTime.TotalMilliseconds, "0-0", _options.BatchSize);
        var reclaimed = claim.ClaimedEntries.Where(e => !e.IsNull).ToArray();
        var fresh = await Redis.StreamReadGroupAsync(_options.StreamKey, _options.ConsumerId, _options.ConsumerName,
            StreamPosition.NewMessages, _options.BatchSize);
        return (reclaimed, fresh);
    }
    internal async Task<InboxApplyOutcome> ApplyAsync(OutboxMessage message, CancellationToken ct)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var first = await new PostgresInboxStore(transaction, _tables)
            .TryRecordAsync(new InboxRecord(_options.ConsumerId, message.EventId, _time.GetUtcNow()), ct);
        if (first)
        {
            await _effect(message, transaction, ct);
        }
        await transaction.CommitAsync(ct);
        return first ? InboxApplyOutcome.Applied : InboxApplyOutcome.Duplicate;
    }
    internal Task AckAsync(RedisValue streamEntryId) =>
        Redis.StreamAcknowledgeAsync(_options.StreamKey, _options.ConsumerId, streamEntryId);
    private async Task<long> DeliveryCountAsync(RedisValue id)
    {
        var pending = await Redis.StreamPendingMessagesAsync(_options.StreamKey, _options.ConsumerId, 1, _options.ConsumerName, id, id);
        return pending.Length == 1 ? pending[0].DeliveryCount : 1;
    }
    /// <summary>Copies the entry to the dead-letter stream (durable, operator-visible), then acknowledges it.</summary>
    private async Task DeadLetterAsync(StreamEntry entry, Guid? eventId, string reason)
    {
        NameValueEntry[] fields =
        [
            .. entry.Values,
            new("dead_source_entry", entry.Id),
            new("dead_reason", reason),
            new("dead_consumer", _options.ConsumerId),
        ];
        var deadId = await Redis.StreamAddAsync(_options.DeadLetterKey, fields);
        await AckAsync(entry.Id);
        _log.DeadLettered(_options.ConsumerId, entry.Id.ToString(), eventId, reason, deadId.ToString());
    }
}
