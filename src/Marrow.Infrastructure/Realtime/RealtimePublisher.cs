using System.Collections.Concurrent;
using System.Text.Json;
using Marrow.Kernel.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Marrow.Infrastructure.Realtime;

/// <summary>
/// Kernel <see cref="IRealtimePublisher"/> over the in-process SignalR hub. The stream's authority supplies the
/// sequence; this class only enforces that it increases per stream and sends events of one stream one at a time, so
/// every subscriber sees them in sequence order. There is no backplane: Realtime runs as exactly one process.
/// </summary>
public sealed class SignalRRealtimePublisher(
    IHubContext<MarrowRealtimeHub, IRealtimeClient> hub,
    TimeProvider time,
    IOptions<MarrowRealtimeOptions> options,
    RealtimeLog log) : IRealtimePublisher
{
    private readonly ConcurrentDictionary<string, StreamGate> _streams = new(StringComparer.Ordinal);

    public async ValueTask<RealtimePublishResult> PublishAsync<TPayload>(
        string streamId, long sequence, string kind, TPayload payload, CancellationToken ct)
    {
        if (!RealtimeWire.TryParseStreamId(streamId, out var streamKind, out _) || sequence < 1)
        {
            return Reject(streamKind, RealtimePublishResult.InvalidStream);
        }
        if (!RealtimeWire.IsValidKind(kind))
        {
            return Reject(streamKind, RealtimePublishResult.InvalidKind);
        }
        if (!RealtimePayload.TryEncode(payload, options.Value.MaxPayloadBytes, out var element))
        {
            return Reject(streamKind, RealtimePublishResult.PayloadTooLarge);
        }
        var gate = _streams.GetOrAdd(streamId, _ => new StreamGate());
        await gate.Lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (sequence <= gate.LastSequence)
            {
                return Reject(streamKind, RealtimePublishResult.SequenceNotIncreasing);
            }
            gate.LastSequence = sequence;
            var envelope = new RealtimeEnvelope<JsonElement>(
                RealtimeEnvelope<JsonElement>.CurrentSchemaVersion,
                streamId,
                RealtimeWire.FormatSequence(sequence),
                Guid.CreateVersion7(time.GetUtcNow()).ToString("D"),
                time.GetUtcNow().ToUniversalTime(),
                kind,
                element);
            await hub.Clients.Group(MarrowRealtimeHub.GroupName(streamId)).Receive(envelope).ConfigureAwait(false);
            return RealtimePublishResult.Published;
        }
        finally
        {
            gate.Lock.Release();
        }
    }

    private RealtimePublishResult Reject(string streamKind, RealtimePublishResult result)
    {
        log.PublishRejected(string.IsNullOrEmpty(streamKind) ? "(invalid)" : streamKind, result);
        return result;
    }

    private sealed class StreamGate
    {
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public long LastSequence { get; set; }
    }
}

/// <summary>
/// Per-connection token bucket on every hub invocation. A throttled call gets the typed <c>rate_limited</c> reply and
/// is not executed. Hub methods all return <see cref="RealtimeReply"/>, so the filter can answer for any of them.
/// </summary>
internal sealed class RealtimeRateLimitFilter(IOptions<MarrowRealtimeOptions> options, TimeProvider time, RealtimeLog log) : IHubFilter
{
    private const string BucketKey = "marrow.realtime.bucket";

    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var context = invocationContext.Context;
        Bucket bucket;
        lock (context.Items)
        {
            if (!context.Items.TryGetValue(BucketKey, out var existing) || existing is not Bucket found)
            {
                found = new Bucket(options.Value.InvocationBurst, time.GetTimestamp());
                context.Items[BucketKey] = found;
            }
            bucket = found;
        }
        if (!bucket.TryTake(options.Value.InvocationsPerSecond, options.Value.InvocationBurst, time))
        {
            log.RateLimited(context.ConnectionId);
            return RealtimeReply.Failure(RealtimeFailure.RateLimited);
        }
        return await next(invocationContext);
    }

    private sealed class Bucket(double tokens, long stamp)
    {
        private double _tokens = tokens;
        private long _stamp = stamp;

        public bool TryTake(double perSecond, int burst, TimeProvider time)
        {
            lock (this)
            {
                var now = time.GetTimestamp();
                _tokens = Math.Min(burst, _tokens + (time.GetElapsedTime(_stamp, now).TotalSeconds * perSecond));
                _stamp = now;
                if (_tokens < 1)
                {
                    return false;
                }
                _tokens -= 1;
                return true;
            }
        }
    }
}

/// <summary>Realtime log events 3201-3211. Arguments are connection ids, stream kinds and enums only: never tokens, cookies or payloads.</summary>
public sealed partial class RealtimeLog(ILogger<RealtimeLog> logger)
{
    [LoggerMessage(EventId = 3201, Level = LogLevel.Information, Message = "Realtime stream access denied: connection {ConnectionId}, stream kind {StreamKind}, reason {Reason}")]
    private static partial void LogStreamDenied(ILogger logger, string connectionId, string streamKind, string reason);

    [LoggerMessage(EventId = 3202, Level = LogLevel.Warning, Message = "Realtime invocation rate limited: connection {ConnectionId}")]
    private static partial void LogRateLimited(ILogger logger, string connectionId);

    [LoggerMessage(EventId = 3203, Level = LogLevel.Warning, Message = "Realtime subscription limit reached: connection {ConnectionId}")]
    private static partial void LogSubscriptionLimit(ILogger logger, string connectionId);

    [LoggerMessage(EventId = 3204, Level = LogLevel.Warning, Message = "Realtime publish rejected: stream kind {StreamKind}, result {Result}")]
    private static partial void LogPublishRejected(ILogger logger, string streamKind, string result);

    [LoggerMessage(EventId = 3205, Level = LogLevel.Warning, Message = "Realtime snapshot rejected (negative sequence or payload too large): stream kind {StreamKind}")]
    private static partial void LogSnapshotRejected(ILogger logger, string streamKind);

    [LoggerMessage(EventId = 3206, Level = LogLevel.Warning, Message = "Realtime connection refused: cookie-authenticated request without an allowed Origin")]
    private static partial void LogOriginRejected(ILogger logger);

    [LoggerMessage(EventId = 3207, Level = LogLevel.Information, Message = "Realtime single-instance room-authority lock acquired (advisory lock {LockKey})")]
    private static partial void LogInstanceLockAcquired(ILogger logger, long lockKey);

    [LoggerMessage(EventId = 3208, Level = LogLevel.Critical, Message = "Realtime refused to start: single-instance room-authority lock not acquired, reason {Reason}")]
    private static partial void LogInstanceLockRefused(ILogger logger, string reason);

    [LoggerMessage(EventId = 3209, Level = LogLevel.Critical, Message = "Realtime room-authority lock connection lost; stopping this instance so no second room authority can overlap")]
    private static partial void LogInstanceLockLost(ILogger logger);

    [LoggerMessage(EventId = 3210, Level = LogLevel.Information, Message = "Realtime connection closed: access no longer verified, connection {ConnectionId}, reason {Reason}")]
    private static partial void LogConnectionClosedForAccess(ILogger logger, string connectionId, string reason);

    [LoggerMessage(EventId = 3211, Level = LogLevel.Information, Message = "Realtime subscription revoked: connection {ConnectionId}, stream kind {StreamKind}, reason {Reason}")]
    private static partial void LogSubscriptionRevoked(ILogger logger, string connectionId, string streamKind, string reason);

    public void ConnectionClosedForAccess(string connectionId, string reason) => LogConnectionClosedForAccess(logger, connectionId, reason);

    public void SubscriptionRevoked(string connectionId, string streamKind, Marrow.Kernel.Security.AccessDenial reason) =>
        LogSubscriptionRevoked(logger, connectionId, streamKind, reason.ToString());

    public void StreamDenied(string connectionId, string streamKind, Marrow.Kernel.Security.AccessDenial reason) =>
        LogStreamDenied(logger, connectionId, streamKind, reason.ToString());

    public void RateLimited(string connectionId) => LogRateLimited(logger, connectionId);

    public void SubscriptionLimit(string connectionId) => LogSubscriptionLimit(logger, connectionId);

    public void PublishRejected(string streamKind, RealtimePublishResult result) => LogPublishRejected(logger, streamKind, result.ToString());

    public void SnapshotRejected(string streamKind) => LogSnapshotRejected(logger, streamKind);

    public void OriginRejected() => LogOriginRejected(logger);

    public void InstanceLockAcquired(long lockKey) => LogInstanceLockAcquired(logger, lockKey);

    public void InstanceLockRefused(string reason) => LogInstanceLockRefused(logger, reason);

    public void InstanceLockLost() => LogInstanceLockLost(logger);
}
