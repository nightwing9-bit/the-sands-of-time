using System.Collections.Immutable;
using GhostLap.Contracts.Multiplayer;

namespace GhostLap.Multiplayer;

/// <summary>
/// The events one tick resolved, linked to the previous tick. Immutable except that the feed cuts <see cref="Previous"/> when a tick leaves the retention
/// window, so a reader that walks the chain either sees a complete run of ticks or reaches the cut and knows older events are gone.
/// </summary>
public sealed class TickFrame(long tick, ImmutableArray<MatchEvent> events)
{
    private TickFrame? _previous;

    public long Tick { get; } = tick;

    public ImmutableArray<MatchEvent> Events { get; } = events;

    public TickFrame? Previous
    {
        get => Volatile.Read(ref _previous);
        internal set => Volatile.Write(ref _previous, value);
    }
}

/// <summary>An authoritative snapshot and the newest <see cref="TickFrame"/> that belongs to it (events through <c>Snapshot.Tick</c>, no later ones).</summary>
public sealed record PublishedSnapshot(MatchSnapshot Snapshot, TickFrame Head);

/// <summary>Events newer than a cursor, up to and including the snapshot's tick, in ascending order.</summary>
public readonly record struct EventSlice(IReadOnlyList<MatchEvent> Items, long FromTick, long ThroughTick, bool Truncated);

/// <summary>
/// The read side of one running match (task 10.3): the latest published authoritative snapshot and a bounded tail of resolved events. The match loop
/// is the only writer; any thread reads without a lock and without blocking the loop. A reader gets one <see cref="PublishedSnapshot"/> (a single
/// volatile read), so the state and the event boundary it returns are the same tick by construction.
/// </summary>
public sealed class MatchFeed
{
    private readonly Queue<TickFrame> _window = new();
    private readonly MultiplayerOptions _options;
    private readonly string _matchId;
    private readonly string _generationId;
    private TickFrame? _head;
    private PublishedSnapshot? _latest;
    private long _sequence = -1;
    private long _headTick;
    private int[] _floors = [];

    public MatchFeed(Guid matchId, Guid generationId, MultiplayerOptions options)
    {
        _matchId = matchId.ToString("N");
        _generationId = generationId.ToString("N");
        _options = options;
    }

    /// <summary>The newest published snapshot, or null before <see cref="PublishInitial"/>.</summary>
    public PublishedSnapshot? Latest => Volatile.Read(ref _latest);

    /// <summary>The last tick the loop resolved (published or not). For snapshot-age telemetry.</summary>
    public long HeadTick => Interlocked.Read(ref _headTick);

    /// <summary>
    /// The sequence floor of a slot as of the newest tick the loop resolved (published or not): never lower than the floor in the latest snapshot.
    /// A controller that resumes after a refresh needs the newest value, because an acknowledgement can be delivered before the next snapshot.
    /// </summary>
    public int CurrentFloor(int slot)
    {
        var floors = Volatile.Read(ref _floors);
        return slot >= 0 && slot < floors.Length ? floors[slot] : -1;
    }

    /// <summary>Loop owner only: the tick-0 state. Sequence 0.</summary>
    public void PublishInitial(MpMatchState state, IReadOnlyList<SlotPresence> presence)
    {
        Publish(0, state, presence, [], isFinal: false, publishSnapshot: true);
    }

    /// <summary>
    /// Loop thread only. Records the tick's events and, when due, publishes a snapshot. Due: every <see cref="MultiplayerOptions.SnapshotIntervalTicks"/>
    /// ticks, and always on the final tick, on a forfeit and on a newly observed disconnect (a presence change must not wait for the interval).
    /// Returns true when a snapshot was published.
    /// </summary>
    public bool Publish(TickOutcome outcome, MpMatchState state)
    {
        var due = outcome.Tick % _options.SnapshotIntervalTicks == 0 || outcome.Final || outcome.Forfeited.Count > 0 || outcome.Neutralized.Count > 0;
        Publish(outcome.Tick, state, outcome.Presence, outcome.Events, outcome.Final, due);
        return due;
    }

    private bool Publish(long tick, MpMatchState state, IReadOnlyList<SlotPresence> presence, IReadOnlyList<MatchEvent> events, bool isFinal, bool publishSnapshot)
    {
        TickFrame head;
        if (tick == 0)
        {
            head = new TickFrame(0, []);
        }
        else
        {
            head = new TickFrame(tick, [.. events]) { Previous = _head };
            _window.Enqueue(head);
            while (_window.Count > _options.EventRetentionTicks)
            {
                _window.Dequeue();
            }
            _window.Peek().Previous = null; // the oldest retained tick ends the chain: nothing before it is reachable or retained
        }
        _head = head;
        Volatile.Write(ref _floors, [.. state.World.Players.Select(p => p.LastSequence)]);
        Interlocked.Exchange(ref _headTick, tick);
        if (!publishSnapshot)
        {
            return false;
        }
        var sequence = ++_sequence;
        Volatile.Write(ref _latest, new PublishedSnapshot(Build(tick, sequence, state, presence, isFinal), head));
        return true;
    }

    private MatchSnapshot Build(long tick, long sequence, MpMatchState state, IReadOnlyList<SlotPresence> presence, bool isFinal)
    {
        var world = state.World;
        var players = new PlayerSnapshot[world.Players.Length];
        for (var i = 0; i < players.Length; i++)
        {
            var p = world.Players[i];
            var forfeitTick = state.ForfeitTicks[i];
            var seen = presence[i];
            players[i] = new PlayerSnapshot(
                i,
                forfeitTick != 0 ? SlotStatuses.Forfeited : seen.Connected ? SlotStatuses.Active : SlotStatuses.Grace,
                seen.Connected,
                p.XFixed,
                p.YFixed,
                p.HeldMoveX,
                p.HeldMoveY,
                p.Charge,
                p.OverlitDeadlineTick,
                p.Pickups,
                p.Bursts,
                p.BurstHits,
                p.MovementPoints,
                p.PickupPoints,
                p.PenaltyPoints,
                p.TotalScore,
                p.LastSequence,
                forfeitTick,
                forfeitTick != 0 ? 0 : seen.GraceEndsTick);
        }
        return new MatchSnapshot(
            _matchId, _generationId, MpRulesProfile.RulesId, MpRulesProfile.LayoutId, tick, sequence, isFinal, players, world.ActiveCandleIds);
    }

    /// <summary>
    /// Events with <c>cursor &lt; Tick &lt;= at.Snapshot.Tick</c>, ascending. When ticks the cursor still needed were already cut from the window,
    /// <see cref="EventSlice.Truncated"/> is true and <see cref="EventSlice.FromTick"/> is the first retained tick. Never throws, never blocks.
    /// </summary>
    public EventSlice EventsAfter(PublishedSnapshot at, long cursor)
    {
        var through = at.Snapshot.Tick;
        var collected = new List<MatchEvent>();
        var frame = at.Head;
        var oldest = frame;
        while (frame is not null && frame.Tick > cursor)
        {
            for (var i = frame.Events.Length - 1; i >= 0; i--)
            {
                collected.Add(frame.Events[i]);
            }
            oldest = frame;
            frame = frame.Previous;
        }
        collected.Reverse();
        // The chain ended (cut or first tick) while still above the cursor: ticks between the cursor and the oldest reachable tick are gone,
        // unless the oldest reachable tick is the first tick of the round (tick 1 has nothing before it).
        var truncated = frame is null && oldest.Tick > Math.Max(cursor + 1, 1);
        var from = truncated ? oldest.Tick : Math.Max(cursor + 1, 1);
        return new EventSlice(collected, from, through, truncated);
    }
}
