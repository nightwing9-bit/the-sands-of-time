using Marrow.Kernel.Messaging;
using Npgsql;
using StackExchange.Redis;
namespace Marrow.Infrastructure.Messaging;
public sealed record OutboxRelayOptions
{
    /// <summary>Redis Stream that receiving hosts consume (one consumer group per consumer id).</summary>
    public required string StreamKey { get; init; }
    public int BatchSize { get; init; } = 50;
    /// <summary>How long a claimed row is reserved for this relay. After it lapses another relay may publish it again.</summary>
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Message-level publish failures (Redis error replies) before the row becomes <c>poisoned</c>.</summary>
    public int MaxAttempts { get; init; } = 8;
    public RetrySchedule Retry { get; init; } = new(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5), 0.2);
    /// <summary>Delay when Redis is unreachable. An outage never counts toward <see cref="MaxAttempts"/>.</summary>
    public RetrySchedule OutageRetry { get; init; } = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), 0.2);
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(StreamKey) || BatchSize is < 1 or > 500 || MaxAttempts is < 1 or > 100
            || LeaseDuration < TimeSpan.FromMilliseconds(100) || LeaseDuration > TimeSpan.FromHours(1))
        {
            throw new ArgumentException("Invalid outbox relay options (StreamKey required, BatchSize 1-500, MaxAttempts 1-100, LeaseDuration 100 ms-1 h).");
        }
    }
}
/// <summary>One relay pass. <see cref="TransportUnavailable"/> means Redis could not be reached and claimed rows were released untouched.</summary>
public sealed record RelayPass(int Claimed, int Published, int Retried, int Poisoned, bool TransportUnavailable);
public sealed record PoisonedOutboxEntry(Guid EventId, string EventType, string CorrelationId, int Attempts, string? LastError, DateTimeOffset PoisonedAt);
internal sealed record ClaimedOutboxEntry(long Seq, OutboxMessage Message, Guid LeaseToken, int Attempts);
/// <summary>
/// Moves committed outbox rows of one module schema to a Redis Stream, at least once. Rows are claimed with
/// <c>FOR UPDATE SKIP LOCKED</c> under a time-bounded lease, so concurrent relays take disjoint rows. A row stays
/// <c>pending</c> until Redis has accepted the entry <i>and</i> the confirming update commits; a crash in between
/// lets the lease lapse and the row is published again, which the consumer inbox deduplicates. Exactly-once delivery
/// is not promised, and ordering across retries is not guaranteed.
/// </summary>
public sealed class OutboxRelay
{
    private readonly NpgsqlDataSource _db;
    private readonly IConnectionMultiplexer _redis;
    private readonly MessagingTables _tables;
    private readonly OutboxRelayOptions _options;
    private readonly MessagingLog _log;
    private readonly Random _random;
    private int _consecutiveOutages;
    public OutboxRelay(NpgsqlDataSource db, IConnectionMultiplexer redis, MessagingTables tables, OutboxRelayOptions options, MessagingLog log, Random? random = null)
    {
        options.Validate();
        (_db, _redis, _tables, _options, _log, _random) = (db, redis, tables, options, log, random ?? Random.Shared);
    }
    public async Task<RelayPass> RunOnceAsync(CancellationToken ct)
    {
        var claimed = await ClaimAsync(ct);
        int published = 0, retried = 0, poisoned = 0;
        for (var i = 0; i < claimed.Count; i++)
        {
            var entry = claimed[i];
            RedisValue streamEntryId;
            try
            {
                streamEntryId = await PublishAsync(entry);
            }
            catch (Exception ex) when (MessagingErrors.IsTransportUnavailable(ex))
            {
                var delay = _options.OutageRetry.Delay(Math.Min(++_consecutiveOutages, 30), _random.NextDouble());
                var error = MessagingErrors.Sanitize(ex);
                foreach (var rest in claimed.Skip(i))
                {
                    await ReleaseAsync(rest, delay, error);
                    _log.TransportUnavailable(rest.Message.EventId, rest.Message.CorrelationId, error, (long)delay.TotalMilliseconds);
                }
                return new RelayPass(claimed.Count, published, retried, poisoned, TransportUnavailable: true);
            }
            catch (RedisServerException ex)
            {
                if (await RecordFailureAsync(entry, MessagingErrors.Sanitize(ex)))
                {
                    poisoned++;
                }
                else
                {
                    retried++;
                }
                continue;
            }
            _consecutiveOutages = 0;
            if (await ConfirmAsync(entry, streamEntryId))
            {
                published++;
                _log.Published(entry.Message.EventId, entry.Message.EventType, entry.Message.CorrelationId, streamEntryId.ToString());
            }
        }
        return new RelayPass(claimed.Count, published, retried, poisoned, TransportUnavailable: false);
    }
    /// <summary>Polls until cancelled. Database or transport errors are logged by class and retried after <paramref name="idleDelay"/>.</summary>
    public async Task RunAsync(TimeSpan idleDelay, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var idle = true;
            try
            {
                var pass = await RunOnceAsync(ct);
                idle = pass.Claimed == 0 || pass.TransportUnavailable;
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException && !ct.IsCancellationRequested)
            {
                _log.TransportUnavailable(Guid.Empty, "-", MessagingErrors.Sanitize(ex), (long)idleDelay.TotalMilliseconds);
            }
            if (idle)
            {
                await Task.Delay(idleDelay, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }
    /// <summary>Operator diagnostics: poisoned rows remain in the table (never deleted) until an owner decides what to do.</summary>
    public async Task<IReadOnlyList<PoisonedOutboxEntry>> ListPoisonedAsync(int limit, CancellationToken ct)
    {
        await using var command = _db.CreateCommand(
            $"SELECT event_id, event_type, correlation_id, attempts, last_error, poisoned_at FROM {_tables.Outbox} WHERE status = 'poisoned' ORDER BY poisoned_at, seq LIMIT @limit");
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 1000));
        var result = new List<PoisonedOutboxEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new PoisonedOutboxEntry(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5)));
        }
        return result;
    }
    internal async Task<IReadOnlyList<ClaimedOutboxEntry>> ClaimAsync(CancellationToken ct)
    {
        await using var command = _db.CreateCommand(
            $"""
            WITH due AS (
                SELECT event_id FROM {_tables.Outbox}
                WHERE status = 'pending' AND next_attempt_at <= now() AND (lease_until IS NULL OR lease_until <= now())
                ORDER BY next_attempt_at, seq
                LIMIT @batch
                FOR UPDATE SKIP LOCKED)
            UPDATE {_tables.Outbox} AS o
            SET lease_token = @token, lease_until = now() + @lease
            FROM due WHERE o.event_id = due.event_id
            RETURNING o.seq, o.event_id, o.event_type, o.event_version, o.aggregate_id, o.occurred_utc, o.payload, o.correlation_id, o.attempts
            """);
        var token = Guid.NewGuid();
        command.Parameters.AddWithValue("batch", _options.BatchSize);
        command.Parameters.AddWithValue("token", token);
        command.Parameters.AddWithValue("lease", _options.LeaseDuration);
        var result = new List<ClaimedOutboxEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var message = new OutboxMessage(reader.GetGuid(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4),
                reader.GetFieldValue<DateTimeOffset>(5), reader.GetString(6), reader.GetString(7));
            result.Add(new ClaimedOutboxEntry(reader.GetInt64(0), message, token, reader.GetInt32(8)));
        }
        result.Sort((a, b) => a.Seq.CompareTo(b.Seq));
        return result;
    }
    internal Task<RedisValue> PublishAsync(ClaimedOutboxEntry entry) =>
        _redis.GetDatabase().StreamAddAsync(_options.StreamKey, StreamEntryCodec.Encode(entry.Message));
    /// <returns>false if this relay no longer holds the lease (another relay re-claimed the row).</returns>
    internal async Task<bool> ConfirmAsync(ClaimedOutboxEntry entry, RedisValue streamEntryId)
    {
        await using var command = _db.CreateCommand(
            $"""
            UPDATE {_tables.Outbox}
            SET status = 'published', published_at = now(), stream_entry_id = @entry, lease_token = NULL, lease_until = NULL, last_error = NULL
            WHERE event_id = @id AND lease_token = @token AND status = 'pending'
            """);
        command.Parameters.AddWithValue("entry", streamEntryId.ToString());
        command.Parameters.AddWithValue("id", entry.Message.EventId);
        command.Parameters.AddWithValue("token", entry.LeaseToken);
        // Not cancellable: once Redis accepted the entry, recording that fact is always attempted.
        var confirmed = await command.ExecuteNonQueryAsync(CancellationToken.None) == 1;
        if (!confirmed)
        {
            _log.LeaseLost(entry.Message.EventId, entry.Message.CorrelationId);
        }
        return confirmed;
    }
    private async Task ReleaseAsync(ClaimedOutboxEntry entry, TimeSpan delay, string error)
    {
        await using var command = _db.CreateCommand(
            $"""
            UPDATE {_tables.Outbox}
            SET lease_token = NULL, lease_until = NULL, next_attempt_at = now() + @delay, last_error = @error
            WHERE event_id = @id AND lease_token = @token AND status = 'pending'
            """);
        command.Parameters.AddWithValue("delay", delay);
        command.Parameters.AddWithValue("error", error);
        command.Parameters.AddWithValue("id", entry.Message.EventId);
        command.Parameters.AddWithValue("token", entry.LeaseToken);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
    /// <returns>true if the row is now poisoned.</returns>
    private async Task<bool> RecordFailureAsync(ClaimedOutboxEntry entry, string error)
    {
        var attempt = entry.Attempts + 1;
        var poison = attempt >= _options.MaxAttempts;
        var delay = poison ? TimeSpan.Zero : _options.Retry.Delay(attempt, _random.NextDouble());
        await using var command = _db.CreateCommand(
            $"""
            UPDATE {_tables.Outbox}
            SET attempts = attempts + 1, lease_token = NULL, lease_until = NULL, last_error = @error,
                next_attempt_at = now() + @delay,
                status = CASE WHEN @poison THEN 'poisoned' ELSE 'pending' END,
                poisoned_at = CASE WHEN @poison THEN now() END
            WHERE event_id = @id AND lease_token = @token AND status = 'pending'
            """);
        command.Parameters.AddWithValue("error", error);
        command.Parameters.AddWithValue("delay", delay);
        command.Parameters.AddWithValue("poison", poison);
        command.Parameters.AddWithValue("id", entry.Message.EventId);
        command.Parameters.AddWithValue("token", entry.LeaseToken);
        if (await command.ExecuteNonQueryAsync(CancellationToken.None) != 1)
        {
            _log.LeaseLost(entry.Message.EventId, entry.Message.CorrelationId);
            return false;
        }
        if (poison)
        {
            _log.Poisoned(entry.Message.EventId, entry.Message.EventType, entry.Message.CorrelationId, attempt, error);
        }
        else
        {
            _log.PublishFailed(entry.Message.EventId, entry.Message.CorrelationId, attempt, error, (long)delay.TotalMilliseconds);
        }
        return poison;
    }
}
