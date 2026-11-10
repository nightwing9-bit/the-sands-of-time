using System.Globalization;
using Marrow.Kernel.Messaging;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
namespace Marrow.Infrastructure.Messaging;
/// <summary>Bounded exponential backoff with bounded downward jitter: the delay lies in <c>[(1-j)·d, d]</c>, <c>d = min(max, base·2^(n-1))</c>.</summary>
public sealed record RetrySchedule
{
    public RetrySchedule(TimeSpan baseDelay, TimeSpan maxDelay, double jitterFraction)
    {
        if (baseDelay <= TimeSpan.Zero || maxDelay < baseDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(baseDelay), "Require 0 < baseDelay <= maxDelay.");
        }
        if (jitterFraction is < 0 or > 0.5 || double.IsNaN(jitterFraction))
        {
            throw new ArgumentOutOfRangeException(nameof(jitterFraction), "Jitter must be within [0, 0.5].");
        }
        BaseDelay = baseDelay;
        MaxDelay = maxDelay;
        JitterFraction = jitterFraction;
    }
    public TimeSpan BaseDelay { get; }
    public TimeSpan MaxDelay { get; }
    public double JitterFraction { get; }
    /// <param name="attempt">1 for the first retry.</param>
    /// <param name="unit">A sample from [0, 1).</param>
    public TimeSpan Delay(int attempt, double unit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        var exponent = Math.Min(attempt - 1, 30);
        var ceiling = Math.Min(MaxDelay.Ticks, BaseDelay.Ticks * Math.Pow(2, exponent));
        var factor = 1 - JitterFraction * Math.Clamp(unit, 0, 1);
        return TimeSpan.FromTicks((long)(ceiling * factor));
    }
}
/// <summary>Reduces an exception to a short diagnostic class, never its free-text message (which can carry data).</summary>
internal static class MessagingErrors
{
    public static string Sanitize(Exception exception)
    {
        var name = exception.GetType().Name;
        if (exception is RedisServerException server)
        {
            // Redis error replies start with an upper-case code (WRONGTYPE, OOM, NOGROUP ...); keep only that code.
            var code = ErrorCode(server);
            return code.Length > 0 ? $"{name}:{code}" : name;
        }
        return name;
    }
    /// <summary>
    /// Redis error replies that describe the server, not the message: loading its append-only file after a restart, busy
    /// script, persistence failure, memory full, read-only replica, failover, or credentials/ACL. They must never count
    /// toward a message's attempts, or a restarting Redis would poison healthy outbox rows.
    /// </summary>
    private static readonly HashSet<string> ServerUnavailableCodes = new(StringComparer.Ordinal)
    {
        "LOADING", "BUSY", "MISCONF", "OOM", "READONLY", "MASTERDOWN", "TRYAGAIN", "CLUSTERDOWN", "NOREPLICAS",
        "NOAUTH", "WRONGPASS", "NOPERM",
    };

    public static bool IsTransportUnavailable(Exception exception) =>
        exception is RedisConnectionException or RedisTimeoutException or TimeoutException or ObjectDisposedException
        || (exception is RedisServerException server && ServerUnavailableCodes.Contains(ErrorCode(server)))
        || (exception is RedisException && exception is not RedisServerException);

    private static string ErrorCode(RedisServerException server) =>
        new(server.Message.TakeWhile(c => c is >= 'A' and <= 'Z').Take(32).ToArray());
}
/// <summary>The Redis Stream entry layout of an Outbox v1 message (one field per contract property).</summary>
internal static class StreamEntryCodec
{
    public const string EventId = "event_id";
    public const string EventType = "event_type";
    public const string EventVersion = "event_version";
    public const string AggregateId = "aggregate_id";
    public const string OccurredUtc = "occurred_utc";
    public const string Payload = "payload";
    public const string CorrelationId = "correlation_id";
    public static NameValueEntry[] Encode(OutboxMessage m) =>
    [
        new(EventId, m.EventId.ToString("D")),
        new(EventType, m.EventType),
        new(EventVersion, m.EventVersion.ToString(CultureInfo.InvariantCulture)),
        new(AggregateId, m.AggregateId),
        new(OccurredUtc, m.OccurredUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
        new(Payload, m.Payload),
        new(CorrelationId, m.CorrelationId),
    ];
    /// <returns>null when the entry is not a well-formed Outbox v1 message.</returns>
    public static OutboxMessage? TryDecode(NameValueEntry[] values)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in values)
        {
            if (entry.Value.IsNull || !map.TryAdd(entry.Name.ToString(), entry.Value.ToString()))
            {
                return null;
            }
        }
        if (map.Count != 7
            || !Guid.TryParseExact(map.GetValueOrDefault(EventId), "D", out var id)
            || !int.TryParse(map.GetValueOrDefault(EventVersion), NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            || !DateTimeOffset.TryParseExact(map.GetValueOrDefault(OccurredUtc), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var occurred))
        {
            return null;
        }
        var message = new OutboxMessage(id, map[EventType], version, map[AggregateId], occurred, map[Payload], map[CorrelationId]);
        try
        {
            PostgresOutboxWriter.Validate(message);
            return message;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
/// <summary>Outbox/inbox log events: ids, types, counts and sanitized error classes only. Never payloads or exception text.</summary>
public sealed partial class MessagingLog(ILogger<MessagingLog> logger)
{
    [LoggerMessage(EventId = 3101, Level = LogLevel.Information, Message = "outbox.published event={EventId} type={EventType} correlation={CorrelationId} stream_entry={StreamEntryId}")]
    public partial void Published(Guid eventId, string eventType, string correlationId, string streamEntryId);
    [LoggerMessage(EventId = 3102, Level = LogLevel.Warning, Message = "outbox.transport_unavailable event={EventId} correlation={CorrelationId} error={Error} retry_in_ms={RetryInMs}")]
    public partial void TransportUnavailable(Guid eventId, string correlationId, string error, long retryInMs);
    [LoggerMessage(EventId = 3103, Level = LogLevel.Warning, Message = "outbox.publish_failed event={EventId} correlation={CorrelationId} attempt={Attempt} error={Error} retry_in_ms={RetryInMs}")]
    public partial void PublishFailed(Guid eventId, string correlationId, int attempt, string error, long retryInMs);
    [LoggerMessage(EventId = 3104, Level = LogLevel.Error, Message = "outbox.poisoned event={EventId} type={EventType} correlation={CorrelationId} attempts={Attempts} error={Error}")]
    public partial void Poisoned(Guid eventId, string eventType, string correlationId, int attempts, string error);
    [LoggerMessage(EventId = 3105, Level = LogLevel.Warning, Message = "outbox.lease_lost event={EventId} correlation={CorrelationId}; published entry may be delivered again (at-least-once)")]
    public partial void LeaseLost(Guid eventId, string correlationId);
    [LoggerMessage(EventId = 3111, Level = LogLevel.Information, Message = "inbox.applied consumer={ConsumerId} event={EventId} correlation={CorrelationId}")]
    public partial void Applied(string consumerId, Guid eventId, string correlationId);
    [LoggerMessage(EventId = 3112, Level = LogLevel.Information, Message = "inbox.duplicate_skipped consumer={ConsumerId} event={EventId} correlation={CorrelationId}")]
    public partial void Duplicate(string consumerId, Guid eventId, string correlationId);
    [LoggerMessage(EventId = 3113, Level = LogLevel.Warning, Message = "inbox.apply_failed consumer={ConsumerId} event={EventId} correlation={CorrelationId} delivery={Delivery} error={Error}")]
    public partial void ApplyFailed(string consumerId, Guid eventId, string correlationId, long delivery, string error);
    [LoggerMessage(EventId = 3114, Level = LogLevel.Error, Message = "inbox.dead_lettered consumer={ConsumerId} stream_entry={StreamEntryId} event={EventId} reason={Reason} dead_letter_entry={DeadLetterEntryId}")]
    public partial void DeadLettered(string consumerId, string streamEntryId, Guid? eventId, string reason, string deadLetterEntryId);
    [LoggerMessage(EventId = 3115, Level = LogLevel.Warning, Message = "inbox.transport_unavailable consumer={ConsumerId} error={Error}")]
    public partial void ConsumerTransportUnavailable(string consumerId, string error);
}
