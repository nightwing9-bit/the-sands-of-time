using System.Globalization;
using System.Text.Json;
using Marrow.Kernel.Security;

namespace Marrow.Kernel.Realtime;

/// <summary>
/// Wire constants of the one realtime hub protocol (v1). The TypeScript mirror is
/// <c>web/packages/contracts/src/realtime.ts</c>; both sides must change together.
/// </summary>
public static class RealtimeWire
{
    public const string HubPath = "/hubs/realtime";

    /// <summary>Server-to-client method carrying one <see cref="RealtimeEnvelope{TPayload}"/>.</summary>
    public const string ReceiveMethod = "Receive";

    public const string SubscribeMethod = "Subscribe";
    public const string UnsubscribeMethod = "Unsubscribe";
    public const string GetSnapshotMethod = "GetSnapshot";

    /// <summary>Exact envelope field names on the wire, in canonical order. No aliases exist.</summary>
    public static readonly IReadOnlyList<string> EnvelopeFields =
        ["schemaVersion", "streamId", "sequence", "eventId", "serverUtc", "kind", "payload"];

    public const int MaxStreamIdLength = 128;
    public const int MaxKindLength = 64;

    /// <summary>Formats a per-stream sequence as the canonical decimal string (no sign, no leading zeros).</summary>
    public static string FormatSequence(long sequence) =>
        sequence < 0 ? throw new ArgumentOutOfRangeException(nameof(sequence)) : sequence.ToString(CultureInfo.InvariantCulture);

    /// <summary>Parses only the canonical decimal form: <c>0</c> or a nonzero digit followed by digits, within <see cref="long"/>.</summary>
    public static bool TryParseSequence(string? text, out long sequence)
    {
        sequence = 0;
        if (string.IsNullOrEmpty(text) || text.Length > 19 || (text.Length > 1 && text[0] == '0'))
        {
            return false;
        }
        foreach (var c in text)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out sequence);
    }

    /// <summary>
    /// A stream id is <c>{kind}:{key}</c>. Kind: lowercase letter then up to 31 of <c>[a-z0-9-]</c>. Key: 1 to 96 of
    /// <c>[A-Za-z0-9_-]</c>. Anything else is rejected before any lookup.
    /// </summary>
    public static bool TryParseStreamId(string? streamId, out string kind, out string key)
    {
        kind = key = string.Empty;
        if (string.IsNullOrEmpty(streamId) || streamId.Length > MaxStreamIdLength)
        {
            return false;
        }
        var colon = streamId.IndexOf(':', StringComparison.Ordinal);
        if (colon < 1 || colon > 32 || streamId.Length - colon - 1 is < 1 or > 96)
        {
            return false;
        }
        if (streamId[0] is < 'a' or > 'z')
        {
            return false;
        }
        for (var i = 1; i < colon; i++)
        {
            if (!(streamId[i] is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
            {
                return false;
            }
        }
        for (var i = colon + 1; i < streamId.Length; i++)
        {
            if (!(streamId[i] is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-'))
            {
                return false;
            }
        }
        kind = streamId[..colon];
        key = streamId[(colon + 1)..];
        return true;
    }

    /// <summary>Event kind: lowercase letter then up to 63 of <c>[a-z0-9._-]</c>.</summary>
    public static bool IsValidKind(string? kind)
    {
        if (string.IsNullOrEmpty(kind) || kind.Length > MaxKindLength || kind[0] is < 'a' or > 'z')
        {
            return false;
        }
        foreach (var c in kind)
        {
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-'))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Strict client-side read of one envelope: exactly the seven <see cref="EnvelopeFields"/>, schema version 1,
    /// canonical decimal-string sequence, valid stream id and kind, ISO-8601 <c>serverUtc</c>. Unknown or missing fields fail.
    /// </summary>
    public static bool TryReadEnvelope(JsonElement element, out RealtimeEnvelope<JsonElement>? envelope, out long sequence)
    {
        envelope = null;
        sequence = 0;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        var seen = 0;
        foreach (var property in element.EnumerateObject())
        {
            if (!EnvelopeFields.Contains(property.Name))
            {
                return false;
            }
            seen++;
        }
        if (seen != EnvelopeFields.Count)
        {
            return false;
        }
        var version = element.GetProperty("schemaVersion");
        var streamId = element.GetProperty("streamId");
        var seq = element.GetProperty("sequence");
        var eventId = element.GetProperty("eventId");
        var serverUtc = element.GetProperty("serverUtc");
        var kind = element.GetProperty("kind");
        if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v) || v != RealtimeEnvelope<JsonElement>.CurrentSchemaVersion
            || streamId.ValueKind != JsonValueKind.String || !TryParseStreamId(streamId.GetString(), out _, out _)
            || seq.ValueKind != JsonValueKind.String || !TryParseSequence(seq.GetString(), out sequence)
            || eventId.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(eventId.GetString())
            || serverUtc.ValueKind != JsonValueKind.String || !serverUtc.TryGetDateTimeOffset(out var at)
            || kind.ValueKind != JsonValueKind.String || !IsValidKind(kind.GetString()))
        {
            return false;
        }
        envelope = new RealtimeEnvelope<JsonElement>(
            v, streamId.GetString()!, seq.GetString()!, eventId.GetString()!, at, kind.GetString()!, element.GetProperty("payload").Clone());
        return true;
    }
}

/// <summary>Authoritative state of one stream at <see cref="Sequence"/>: every event with a higher sequence is newer.</summary>
public sealed record RealtimeSnapshot<TPayload>(int SchemaVersion, string StreamId, string Sequence, TPayload Payload);

/// <summary>Typed hub reply. Failure codes are <see cref="RealtimeFailure"/> constants; no internal reason is exposed.</summary>
public sealed record RealtimeReply(bool Ok, string? Code, RealtimeSnapshot<JsonElement>? Snapshot)
{
    public static RealtimeReply Success(RealtimeSnapshot<JsonElement>? snapshot = null) => new(true, null, snapshot);

    public static RealtimeReply Failure(string code) => new(false, code, null);
}

public static class RealtimeFailure
{
    /// <summary>Not allowed, unknown stream kind or unknown stream: deliberately indistinguishable.</summary>
    public const string Forbidden = "forbidden";
    public const string InvalidStream = "invalid_stream";
    public const string RateLimited = "rate_limited";
    public const string SubscriptionLimit = "subscription_limit";
    public const string Unavailable = "unavailable";
}

/// <summary>Which domain-owned authorization decides access to a stream. Exactly one of the two is set.</summary>
public readonly record struct RealtimeStreamAccess
{
    private RealtimeStreamAccess(MembershipScope? membership, ResourceRef? resource)
    {
        Membership = membership;
        Resource = resource;
    }

    public MembershipScope? Membership { get; }
    public ResourceRef? Resource { get; }

    /// <summary>Subscribers must be members of the scope, checked through <see cref="IMembershipAuthorizer"/>.</summary>
    public static RealtimeStreamAccess RequireMembership(MembershipScope scope) => new(scope, null);

    /// <summary>Subscribers must be allowed to read the resource, checked through <see cref="IResourceAuthorizer"/>.</summary>
    public static RealtimeStreamAccess RequireRead(ResourceRef resource) => new(null, resource);
}

/// <summary>Snapshot produced by the stream's authority. <see cref="Sequence"/> is the last sequence it has published or will publish.</summary>
public sealed record RealtimeSourceSnapshot(long Sequence, object? Payload);

/// <summary>
/// Implemented by the domain that owns one stream kind (for example a room loop). It is the only authority for the
/// stream's state and sequence; the hub never computes or stores state. Snapshot and sequence must be consistent:
/// the payload reflects exactly the events up to and including <see cref="RealtimeSourceSnapshot.Sequence"/>.
/// </summary>
public interface IRealtimeStreamSource
{
    /// <summary>Stream kind, the part of the stream id before the colon.</summary>
    string StreamKind { get; }

    /// <summary>Maps a stream key to the domain authorization that guards it. Never derived from caller-supplied owner ids.</summary>
    RealtimeStreamAccess ResolveAccess(string streamKey);

    /// <summary>Current snapshot, or null when the stream does not exist.</summary>
    ValueTask<RealtimeSourceSnapshot?> GetSnapshotAsync(string streamId, CancellationToken ct);
}

public enum RealtimePublishResult
{
    Published,
    InvalidStream,
    InvalidKind,
    /// <summary>The sequence is not greater than the last one published on this stream.</summary>
    SequenceNotIncreasing,
    PayloadTooLarge,
}

/// <summary>
/// Sends one event of a stream to its current subscribers. Delivery is best effort: transport can drop or repeat,
/// and clients recover through a snapshot. Never use it for business effects that must happen exactly once.
/// </summary>
public interface IRealtimePublisher
{
    ValueTask<RealtimePublishResult> PublishAsync<TPayload>(string streamId, long sequence, string kind, TPayload payload, CancellationToken ct);
}

public enum RealtimeCursorAction
{
    /// <summary>Next in order: apply it.</summary>
    Apply,
    /// <summary>Already covered by the snapshot or an earlier event: ignore it.</summary>
    Duplicate,
    /// <summary>Held until the pending snapshot arrives.</summary>
    Buffered,
    /// <summary>A sequence was skipped. The event is buffered and the caller must fetch a snapshot.</summary>
    Gap,
}

public sealed record RealtimeSnapshotOutcome<T>(bool Accepted, IReadOnlyList<T> Ready, bool GapRemains);

/// <summary>
/// Client-side ordering state for one stream (pure, transport-agnostic). Starts awaiting a snapshot. Events that arrive
/// before the snapshot are buffered (bounded), events at or below the applied sequence are duplicates, the next sequence
/// is applied, anything further ahead is a gap that requires a snapshot. The TypeScript twin has the same rules.
/// </summary>
public sealed class RealtimeStreamCursor<T>
{
    private readonly SortedDictionary<long, T> _buffer = [];
    private readonly int _maxBuffered;

    public RealtimeStreamCursor(string streamId, int maxBuffered = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBuffered, 1);
        StreamId = streamId;
        _maxBuffered = maxBuffered;
    }

    public string StreamId { get; }

    /// <summary>Last applied sequence, or null before the first snapshot.</summary>
    public long? Sequence { get; private set; }

    public bool AwaitingSnapshot { get; private set; } = true;

    /// <summary>Forget order state (connection lost). The next snapshot re-establishes it.</summary>
    public void Reset()
    {
        _buffer.Clear();
        AwaitingSnapshot = true;
        Sequence = null;
    }

    public RealtimeCursorAction Accept(long sequence, T item)
    {
        if (Sequence is { } applied && sequence <= applied)
        {
            return RealtimeCursorAction.Duplicate;
        }
        if (AwaitingSnapshot)
        {
            Buffer(sequence, item);
            return RealtimeCursorAction.Buffered;
        }
        if (sequence == Sequence + 1)
        {
            Sequence = sequence;
            return RealtimeCursorAction.Apply;
        }
        AwaitingSnapshot = true;
        Buffer(sequence, item);
        return RealtimeCursorAction.Gap;
    }

    /// <summary>
    /// Applies a snapshot taken at <paramref name="sequence"/>. A snapshot older than the applied sequence is refused
    /// (<c>Accepted = false</c>). Returns the buffered events that now follow in order; if a buffered event is still
    /// ahead of them, <c>GapRemains</c> is true and the cursor waits for another snapshot.
    /// </summary>
    public RealtimeSnapshotOutcome<T> ApplySnapshot(long sequence)
    {
        if (Sequence is { } applied && sequence < applied)
        {
            return new RealtimeSnapshotOutcome<T>(false, [], AwaitingSnapshot);
        }
        Sequence = sequence;
        var ready = new List<T>();
        foreach (var key in _buffer.Keys.ToArray())
        {
            if (key <= Sequence)
            {
                _buffer.Remove(key);
            }
            else if (key == Sequence + 1)
            {
                ready.Add(_buffer[key]);
                _buffer.Remove(key);
                Sequence = key;
            }
            else
            {
                break;
            }
        }
        AwaitingSnapshot = _buffer.Count > 0;
        return new RealtimeSnapshotOutcome<T>(true, ready, AwaitingSnapshot);
    }

    private void Buffer(long sequence, T item)
    {
        if (_buffer.Count >= _maxBuffered && !_buffer.ContainsKey(sequence))
        {
            // Bounded: drop the newest; the snapshot that must follow covers it.
            var last = _buffer.Keys.Last();
            if (sequence > last)
            {
                return;
            }
            _buffer.Remove(last);
        }
        _buffer[sequence] = item;
    }
}
