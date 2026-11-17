namespace GhostLap.Multiplayer;

/// <summary>Percentiles of one measured series. Counts and maxima cover every sample; percentiles cover the newest retained window.</summary>
public readonly record struct SeriesSummary(long Count, long Window, long P50, long P95, long P99, long Max);

/// <summary>
/// A fixed-capacity window of the newest samples plus an all-time count and maximum. Allocation-free after construction. Thread-safe with a
/// short lock: the match loop writes one sample per tick and the profile reporter reads rarely, so the lock is effectively uncontended.
/// Observation only: nothing here influences a tick, a schedule or a limit.
/// </summary>
public sealed class SampleSeries(int capacity = SampleSeries.DefaultCapacity)
{
    /// <summary>Enough for one whole 2700-tick round (the default <c>SnapshotIntervalTicks</c> sends fewer samples than ticks).</summary>
    public const int DefaultCapacity = 4096;

    private readonly long[] _ring = new long[capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity))];
    private readonly object _gate = new();
    private long _count;
    private long _max;

    public void Add(long value)
    {
        lock (_gate)
        {
            _ring[_count % _ring.Length] = value;
            _count++;
            if (value > _max || _count == 1)
            {
                _max = value;
            }
        }
    }

    public SeriesSummary Summarize()
    {
        long[] copy;
        long count;
        long max;
        lock (_gate)
        {
            count = _count;
            max = _max;
            var window = (int)Math.Min(count, _ring.Length);
            copy = new long[window];
            Array.Copy(_ring, copy, window); // while the ring is not full, indices 0..window-1 are exactly the samples; once full, all of them are
        }
        if (copy.Length == 0)
        {
            return default;
        }
        Array.Sort(copy);
        return new SeriesSummary(count, copy.Length, Rank(copy, 50), Rank(copy, 95), Rank(copy, 99), max);
    }

    /// <summary>Nearest-rank percentile (the smallest sample with at least p percent of samples at or below it).</summary>
    private static long Rank(long[] sorted, int percent)
    {
        var index = (int)(((long)percent * sorted.Length) + 99) / 100 - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }
}

/// <summary>Per-match measurements for the task 10.5 profile. Counters and series only; no behavior.</summary>
public sealed class MatchTelemetry
{
    private long _bytesSent;
    private long _ticks;

    /// <summary>Wall time one <see cref="MatchEngine.Step"/> plus its non-blocking publication took on the injected monotonic clock (microseconds).</summary>
    public SampleSeries TickExecutionMicros { get; } = new();

    /// <summary>How long after its due time a tick actually started (microseconds, never negative).</summary>
    public SampleSeries StartLatenessMicros { get; } = new();

    /// <summary>Ticks that were already due and still waiting behind the tick that just started (0 = on time, the scheduler is not behind).</summary>
    public SampleSeries BacklogTicks { get; } = new();

    /// <summary>Queued, unapplied gameplay items in the room mailbox when the tick started.</summary>
    public SampleSeries MailboxDepth { get; } = new();

    /// <summary>Estimated JSON size of each snapshot frame one recipient was sent (only when <c>MeasureSnapshotBytes</c> is on).</summary>
    public SampleSeries SnapshotBytes { get; } = new(16_384);

    public long Ticks => Interlocked.Read(ref _ticks);

    public long BytesSent => Interlocked.Read(ref _bytesSent);

    public void RecordTick(long executionMicros, long latenessMicros, long backlogTicks, long mailboxDepth)
    {
        TickExecutionMicros.Add(executionMicros);
        StartLatenessMicros.Add(latenessMicros);
        BacklogTicks.Add(backlogTicks);
        MailboxDepth.Add(mailboxDepth);
        Interlocked.Increment(ref _ticks);
    }

    public void RecordSnapshotBytes(long bytes)
    {
        SnapshotBytes.Add(bytes);
        Interlocked.Add(ref _bytesSent, bytes);
    }

}

/// <summary>Recipient counters of one match summed over its current recipients (closed recipients are not included).</summary>
public sealed record RecipientTotals(
    int Recipients,
    long SnapshotsSent,
    long SnapshotsSkipped,
    long ResyncFrames,
    long AcksSent,
    long AcksCoalesced,
    long RejectionsSent,
    long RejectionsDropped,
    long Failures,
    int Stalled,
    int ControlQueueDepth,
    long MaxSendMicroseconds,
    long MaxSnapshotAgeTicks);

public sealed record MatchLoad(
    string MatchId,
    string Status,
    int Players,
    long Tick,
    long MaxBacklogTicks,
    long CatchUpIterations,
    long LateTicks,
    SeriesSummary TickExecutionMicros,
    SeriesSummary StartLatenessMicros,
    SeriesSummary BacklogTicks,
    SeriesSummary MailboxDepth,
    SeriesSummary SnapshotBytes,
    long BytesSent,
    long TotalAcceptedInputs,
    long TotalRejectedInputs,
    int PendingInputs,
    RecipientTotals Recipients,
    IReadOnlyList<RecipientLoad> PerRecipient);

public sealed record RecipientLoad(
    string MemberId,
    int Slot,
    int ControlQueueDepth,
    int MaxControlQueueDepth,
    long SnapshotsSent,
    long SnapshotsSkipped,
    long ResyncFrames,
    long AcksCoalesced,
    long Failures,
    bool Stalled);

/// <summary>Admission counters. A denial is counted, never partially created (no room, receipt or runtime exists for a denied start).</summary>
public sealed record CapacityLoad(int LiveMatches, int ReservedStarts, int MaxConcurrentMatches, long StartsDeniedForCapacity, long ConnectionsRefusedForCapacity);

/// <summary>What the registry can see: admission counters and every match's series. Process-wide GC/CPU are added by the host, which owns the process.</summary>
public sealed record MatchLoadReport(CapacityLoad Capacity, IReadOnlyList<MatchLoad> Matches);
