namespace GhostLap.Contracts.Multiplayer;

public enum SnapshotVerdict
{
    /// <summary>A newer frame of the current match and generation: apply it.</summary>
    Accepted,

    /// <summary>A resync (or the first frame): it replaces the baseline.</summary>
    Baseline,

    /// <summary>Not newer than what was already applied (older lifecycle, or not a higher sequence): discard.</summary>
    Stale,

    /// <summary>Another match than the baseline's and not a resync: discard. A new match is adopted only by an explicit resync.</summary>
    ForeignMatch,

    /// <summary>Same match, other authority generation, not a resync: discard. Generation ids are opaque and never compared by magnitude.</summary>
    ForeignGeneration,
}

/// <summary>The decision on one frame and the gameplay cues it newly delivers (already de-duplicated).</summary>
public sealed record FilterResult(SnapshotVerdict Verdict, IReadOnlyList<MatchEvent> NewEvents, bool EventGap)
{
    public bool Applied => Verdict is SnapshotVerdict.Accepted or SnapshotVerdict.Baseline;
}

/// <summary>
/// The causality filter a client applies to server frames (the C# reference of the rules the TypeScript client must follow, task 10.6). Pure and
/// single-threaded: one instance per match view.
/// <list type="bullet">
/// <item>Identity is the pair <c>(MatchId, GenerationId)</c>, compared for equality only. Generation ids are opaque and never ordered.</item>
/// <item>Order inside an identity is <c>(LifecycleVersion, Sequence)</c>. A pushed frame must not lower either and must raise one of them. A resync must
/// not lower either but may repeat both (it is the same state).</item>
/// <item>A frame of another match or generation is discarded unless it is a resync, which replaces the baseline (this is how a rematch is adopted).</item>
/// <item>Events apply once: only keys above the baseline's high-water key are returned. A truncated batch, or a batch that starts after a hole,
/// is reported as <see cref="FilterResult.EventGap"/> (cues were lost; state is still exact).</item>
/// </list>
/// A delayed, duplicated or superseded frame therefore changes nothing: gameplay effects come from state, which is replaced and never replayed.
/// </summary>
public sealed class SnapshotFilter
{
    private string? _matchId;
    private string? _generationId;
    private long _lifecycle = -1;
    private long _sequence = -1;
    private long _eventKey = -1;
    private long _eventThroughTick;

    public string? MatchId => _matchId;

    public string? GenerationId => _generationId;

    public long LifecycleVersion => _lifecycle;

    public long Sequence => _sequence;

    public long EventHighWaterKey => _eventKey;

    public FilterResult Offer(SnapshotFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var snapshot = frame.Snapshot;
        if (_matchId is null)
        {
            return Adopt(frame, SnapshotVerdict.Baseline, resetEvents: true);
        }
        var sameMatch = string.Equals(snapshot.MatchId, _matchId, StringComparison.Ordinal);
        var sameGeneration = string.Equals(snapshot.GenerationId, _generationId, StringComparison.Ordinal);
        if (sameMatch && sameGeneration)
        {
            var monotone = frame.LifecycleVersion >= _lifecycle && snapshot.Sequence >= _sequence;
            var advances = frame.LifecycleVersion > _lifecycle || snapshot.Sequence > _sequence;
            if (!monotone || (!frame.IsResync && !advances))
            {
                return new FilterResult(SnapshotVerdict.Stale, [], false);
            }
            return Adopt(frame, frame.IsResync ? SnapshotVerdict.Baseline : SnapshotVerdict.Accepted, resetEvents: false);
        }
        if (!frame.IsResync)
        {
            return new FilterResult(sameMatch ? SnapshotVerdict.ForeignGeneration : SnapshotVerdict.ForeignMatch, [], false);
        }
        // An explicit resync of another match or generation is the server's current truth: adopt it with a fresh event baseline.
        return Adopt(frame, SnapshotVerdict.Baseline, resetEvents: true);
    }

    private FilterResult Adopt(SnapshotFrame frame, SnapshotVerdict verdict, bool resetEvents)
    {
        _matchId = frame.Snapshot.MatchId;
        _generationId = frame.Snapshot.GenerationId;
        _lifecycle = frame.LifecycleVersion;
        _sequence = frame.Snapshot.Sequence;
        if (resetEvents)
        {
            _eventKey = -1;
            _eventThroughTick = 0;
        }
        var batch = frame.Events;
        var gap = !frame.IsResync && (batch.Truncated || (_eventThroughTick > 0 && batch.FromTick > _eventThroughTick + 1));
        var fresh = new List<MatchEvent>();
        foreach (var e in batch.Items)
        {
            if (e.Key > _eventKey)
            {
                fresh.Add(e);
                _eventKey = e.Key;
            }
        }
        _eventThroughTick = Math.Max(_eventThroughTick, batch.ThroughTick);
        return new FilterResult(verdict, fresh, gap);
    }
}
