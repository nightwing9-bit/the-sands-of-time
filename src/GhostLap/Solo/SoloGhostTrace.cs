using GhostLap.Rules;
using GhostLap.Timeline;
using Marrow.Timeline;

namespace GhostLap.Solo;

/// <summary>
/// The complete, immutable T0..T2700 historical trace of one verified <c>ghost-v1</c> original, held in memory for a
/// challenge. Tn is the state after n transitions (T0 is the initial state), so <see cref="At"/> is a plain array index:
/// reading it touches no database, restore, trace or compare call. Frames are read-only data; nothing here is a collider
/// or can change a living world.
/// </summary>
public sealed class PreparedGhostTrace
{
    private readonly SoloTraceFrame[] _frames;

    internal PreparedGhostTrace(TimelineId original, string rulesVersion, string layoutVersion, string finalStateHash, SoloTraceFrame[] frames)
    {
        if (frames.Length != GameRulesV1.RoundTicks + 1)
        {
            throw new ArgumentException($"A prepared trace has exactly {GameRulesV1.RoundTicks + 1} frames.", nameof(frames));
        }

        Original = original;
        RulesVersion = rulesVersion;
        LayoutVersion = layoutVersion;
        FinalStateHash = finalStateHash;
        _frames = frames;
    }

    public TimelineId Original { get; }

    /// <summary>The pinned solo Timeline key; the only one a trace is ever built under.</summary>
    public TimelineVersionKey Versions => GhostLapTimelineVersions.V1;

    public string RulesVersion { get; }

    public string LayoutVersion { get; }

    /// <summary>The verified canonical state hash after tick 2700 of the source.</summary>
    public string FinalStateHash { get; }

    public int FrameCount => _frames.Length;

    /// <summary>The source frame after exactly <paramref name="tick"/> transitions (0..2700), by direct indexing.</summary>
    public SoloTraceFrame At(long tick)
    {
        if ((ulong)tick >= (ulong)_frames.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(tick), tick, $"Tick must be 0-{_frames.Length - 1}.");
        }

        return _frames[tick];
    }

    /// <summary>Reuse rule: same original, pinned rules/layout/codec and verified final hash as the freshly read row.</summary>
    internal bool Matches(SoloOriginalRecord row) =>
        Original == row.Timeline && Versions == GhostLapTimelineVersions.V1 && RulesVersion == row.RulesVersion
        && LayoutVersion == row.LayoutVersion && row.Status == SoloOriginalStatus.Complete && FinalStateHash == row.FinalStateHash;

    internal bool Matches(PreparedGhostTrace other) =>
        Original == other.Original && Versions == other.Versions && RulesVersion == other.RulesVersion
        && LayoutVersion == other.LayoutVersion && FinalStateHash == other.FinalStateHash;
}

/// <summary>A hold on a cached trace. Disposing it (idempotent) lets the cache evict the trace again.</summary>
public sealed class PreparedTraceLease : IDisposable
{
    private SoloGhostTraceCache? _cache;
    private readonly object _entry;

    internal PreparedTraceLease(SoloGhostTraceCache cache, object entry, PreparedGhostTrace trace)
    {
        _cache = cache;
        _entry = entry;
        Trace = trace;
    }

    public PreparedGhostTrace Trace { get; }

    public void Dispose()
    {
        var cache = Interlocked.Exchange(ref _cache, null);
        cache?.Release(_entry);
    }
}

/// <summary>
/// Process-local, bounded cache of prepared traces (no database, no archive). An entry is owner-bound, is reused only when the
/// requested original's freshly read row still matches (<see cref="PreparedGhostTrace.Matches(SoloOriginalRecord)"/>), is never
/// evicted while a challenge leases it, and idle entries expire. When every slot is leased a new preparation is refused
/// instead of growing memory.
/// </summary>
public sealed class SoloGhostTraceCache(int capacity = SoloGhostTraceCache.DefaultCapacity, TimeSpan? idleLifetime = null, TimeProvider? time = null)
{
    public const int DefaultCapacity = 4;
    public static readonly TimeSpan DefaultIdleLifetime = TimeSpan.FromMinutes(5);

    private sealed class Entry(PreparedGhostTrace trace, long now)
    {
        public PreparedGhostTrace Trace { get; } = trace;

        public int Leases;

        public long LastUsed = now;
    }

    private readonly int _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly TimeSpan _idle = idleLifetime ?? DefaultIdleLifetime;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<(string Owner, TimelineId Original), Entry> _entries = [];
    private readonly Lock _gate = new();

    /// <summary>Entries currently held (leased or idle).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                Sweep();
                return _entries.Count;
            }
        }
    }

    /// <summary>Entries with at least one active lease.</summary>
    public int LeasedCount
    {
        get
        {
            lock (_gate)
            {
                return _entries.Values.Count(e => e.Leases > 0);
            }
        }
    }

    internal PreparedTraceLease? TryLease(string owner, SoloOriginalRecord row)
    {
        lock (_gate)
        {
            Sweep();
            if (_entries.TryGetValue((owner, row.Timeline), out var entry) && entry.Trace.Matches(row))
            {
                return Lease(entry);
            }

            return null;
        }
    }

    /// <summary>Adds (or joins an identical concurrent) entry; null when the cache is full of leased traces.</summary>
    internal PreparedTraceLease? TryAdd(string owner, PreparedGhostTrace trace)
    {
        lock (_gate)
        {
            Sweep();
            var key = (owner, trace.Original);
            if (_entries.TryGetValue(key, out var existing))
            {
                if (existing.Trace.Matches(trace))
                {
                    return Lease(existing);
                }

                if (existing.Leases > 0)
                {
                    return null;
                }

                _entries.Remove(key);
            }

            if (_entries.Count >= _capacity)
            {
                (string Owner, TimelineId Original)? victim = null;
                var oldest = long.MaxValue;
                foreach (var (k, e) in _entries)
                {
                    if (e.Leases == 0 && e.LastUsed < oldest)
                    {
                        (victim, oldest) = (k, e.LastUsed);
                    }
                }

                if (victim is null)
                {
                    return null;
                }

                _entries.Remove(victim.Value);
            }

            var entry = new Entry(trace, _time.GetTimestamp());
            _entries[key] = entry;
            return Lease(entry);
        }
    }

    /// <summary>Drops every idle entry now (leased ones stay until released).</summary>
    public void ReleaseIdle()
    {
        lock (_gate)
        {
            foreach (var key in _entries.Where(e => e.Value.Leases == 0).Select(e => e.Key).ToList())
            {
                _entries.Remove(key);
            }
        }
    }

    internal void Release(object entry)
    {
        lock (_gate)
        {
            if (entry is Entry e && e.Leases > 0)
            {
                e.Leases--;
                e.LastUsed = _time.GetTimestamp();
            }
        }
    }

    private PreparedTraceLease Lease(Entry entry)
    {
        entry.Leases++;
        entry.LastUsed = _time.GetTimestamp();
        return new PreparedTraceLease(this, entry, entry.Trace);
    }

    private void Sweep()
    {
        var now = _time.GetTimestamp();
        foreach (var key in _entries.Where(e => e.Value.Leases == 0 && _time.GetElapsedTime(e.Value.LastUsed, now) >= _idle)
                     .Select(e => e.Key).ToList())
        {
            _entries.Remove(key);
        }
    }
}
