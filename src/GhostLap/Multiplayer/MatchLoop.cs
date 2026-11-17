using System.Threading.Channels;
using GhostLap.Contracts.Multiplayer;
using GhostLap.Rules;
using GhostLap.Simulation;

namespace GhostLap.Multiplayer;

/// <summary>An applied-input acknowledgement for one controlling connection, produced by a tick.</summary>
public readonly record struct AckItem(string ConnectionId, int Slot, long ControllerEpoch, long Tick, int LastAppliedSeq);

/// <summary>
/// An input the match loop refused when it tried to apply it. <paramref name="SequenceFloor"/> is the highest sequence applied for the slot at that
/// moment (a server neutral included), so the client can resume above it.
/// </summary>
public readonly record struct RejectItem(string ConnectionId, int Slot, int Seq, string Code, int SequenceFloor = -1);

/// <summary>
/// One slot's connection state as the loop read it for this tick. <see cref="GraceEndsTick"/> is the tick F at which a still-unconnected slot is
/// forfeited (0 when connected or already forfeited).
/// </summary>
public readonly record struct SlotPresence(bool Connected, long GraceEndsTick);

/// <summary>
/// What one authoritative tick produced. <see cref="Applied"/> are the exact inputs given to the reducer (the server neutral of a
/// disconnect included); they exist only in memory for acknowledgements and tests, never in an archive (no MP input journal).
/// <see cref="Forfeited"/> are the slots whose forfeit took effect in phase 0 of this tick, <see cref="Events"/> the resolved events of this tick with
/// their stable identity, <see cref="Presence"/> every slot's connection state for this tick.
/// </summary>
public sealed record TickOutcome(
    long Tick,
    IReadOnlyList<GameInput> Applied,
    IReadOnlyList<AckItem> Acks,
    IReadOnlyList<RejectItem> Rejections,
    IReadOnlyList<int> Neutralized,
    bool Final,
    IReadOnlyList<int> Forfeited,
    IReadOnlyList<MatchEvent> Events,
    IReadOnlyList<SlotPresence> Presence);

/// <summary>
/// The authoritative world of one match and its serial mailbox (design section 1: one serial mailbox per room).
/// <para>
/// Threads: <see cref="TryEnqueue"/> is called by socket readers and only validates cheap structure, applies the per-slot budget and
/// writes into a bounded channel (never blocks). <see cref="Step"/> is called by exactly one loop and is the only code that reads or
/// changes the world. At application time it re-reads each slot's controller (epoch and connection), so an input queued by a
/// connection that was superseded or closed after queueing cannot affect the world.
/// </para>
/// </summary>
public sealed class MatchEngine
{
    private readonly Channel<AdmittedInput> _mailbox;
    private readonly Dictionary<string, int> _slotByMember = new(StringComparer.Ordinal);
    private readonly SlotControlCell[] _controllers;
    private readonly bool[] _lastConnected;
    private readonly long[] _graceStart;
    private readonly InputBudget[] _budgets;
    private readonly MultiplayerOptions _options;
    private readonly string _matchIdText;

    public MatchEngine(SealedRoundConfig config, MultiplayerOptions options, TimeProvider time)
        : this(config, options, time, null)
    {
    }

    /// <summary>Test seam (internal): starts from a prepared world, for example one with an Overlit slot, instead of replaying thousands of ticks.</summary>
    internal MatchEngine(SealedRoundConfig config, MultiplayerOptions options, TimeProvider time, MpMatchState? initial)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(options);
        Config = config;
        Context = MpMatchContext.From(config);
        State = initial ?? MpMatchState.Initial(Context);
        _options = options;
        _matchIdText = config.MatchId.ToString("N");
        var n = Context.PlayerCount;
        _controllers = new SlotControlCell[n];
        // Every competitor is connected when a round starts (the room starts only with all of them connected). A slot whose controller is
        // missing at the first tick is therefore a disconnect that happened during admission and starts its grace at that tick.
        _lastConnected = [.. Enumerable.Repeat(true, n)];
        _graceStart = new long[n];
        _budgets = new InputBudget[n];
        for (var i = 0; i < n; i++)
        {
            _controllers[i] = new SlotControlCell(i);
            _budgets[i] = new InputBudget(options, time);
            _slotByMember[config.Slots[i].MemberId] = i;
        }
        _mailbox = Channel.CreateBounded<AdmittedInput>(new BoundedChannelOptions(options.MailboxCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait, // TryWrite fails when full; nothing ever waits
            SingleReader = true,
        });
    }

    public SealedRoundConfig Config { get; }

    public MpMatchContext Context { get; }

    /// <summary>The authoritative state after the last executed tick. Written only by <see cref="Step"/>.</summary>
    public MpMatchState State { get; private set; }

    public int QueuedInputs => _mailbox.Reader.Count;

    public long TotalRejected { get; private set; }

    /// <summary>Set by the room owner (under its lock) through the registry. Read by <see cref="Step"/> at application time.</summary>
    public void SetController(SlotControl control)
    {
        if (control.Slot >= 0 && control.Slot < _controllers.Length)
        {
            _controllers[control.Slot].Set(control.Epoch, control.ConnectionId);
        }
    }

    public SlotControl ControllerOf(int slot) => _controllers[slot].Current;

    public int? SlotOf(string memberId) => _slotByMember.TryGetValue(memberId, out var slot) ? slot : null;

    /// <summary>Ingress (any thread). Null = queued for the match loop; otherwise a typed rejection.</summary>
    public string? TryEnqueue(in AdmittedInput input)
    {
        if (!string.Equals(input.Input.MatchId, _matchIdText, StringComparison.OrdinalIgnoreCase)
            && !(Guid.TryParse(input.Input.MatchId, out var id) && id == Config.MatchId))
        {
            return GhostLapFailure.WrongMatch;
        }
        if (SlotOf(input.MemberId) is not { } slot)
        {
            return GhostLapFailure.Forbidden;
        }
        // Aggregate per-slot budget across every connection the principal holds (the per-connection budget is in the hub).
        var budget = _budgets[slot];
        lock (budget)
        {
            if (!budget.TryTake())
            {
                return GhostLapFailure.RateLimited;
            }
        }
        return _mailbox.Writer.TryWrite(input) ? null : GhostLapFailure.MailboxFull;
    }

    /// <summary>
    /// Executes exactly one logical tick: <c>State.Tick + 1</c>. Order: (1) read every slot's controller once. A slot whose controller closed since
    /// the previous tick gets the Spec 06 server neutral for this tick (immediate, no six-tick hold) and that tick D starts its grace; a slot that
    /// is still unconnected when tick <c>D + 450</c> begins is forfeited in phase 0 of that tick; a controller present at that read keeps the slot
    /// (a takeover processed before the tick resolves wins, one after it is spectator-only). (2) drain the mailbox in arrival order and admit each
    /// item against the controllers read in (1) and the Spec 06 admission policy; (3) run the ghost-mp-v1 reducer; (4) emit acknowledgements and
    /// events. Pure CPU: no I/O, no clock, no await.
    /// </summary>
    public TickOutcome Step()
    {
        if (State.IsFinal)
        {
            throw new InvalidOperationException("The match already resolved its final tick.");
        }
        var world = State.World;
        var n = Context.PlayerCount;
        var next = State.Tick + 1;
        var controls = new SlotControl[n];
        var pending = new List<GameInput>();
        var neutralized = new List<int>();
        var forfeitNow = new List<int>();
        for (var slot = 0; slot < n; slot++)
        {
            controls[slot] = _controllers[slot].Current;
            var connected = controls[slot].ConnectionId is not null;
            if (!State.IsForfeited(slot))
            {
                if (connected)
                {
                    _graceStart[slot] = 0; // a controller returned before tick F: the slot is kept
                }
                else
                {
                    if (_lastConnected[slot])
                    {
                        pending.Add(InputAdmission.Neutralize(world, pending, (byte)slot));
                        neutralized.Add(slot);
                        _graceStart[slot] = next; // D: ticks D..D+449 are grace
                    }
                    if (_graceStart[slot] != 0 && next >= _graceStart[slot] + MpRulesProfile.GraceTicks)
                    {
                        forfeitNow.Add(slot);
                    }
                }
            }
            _lastConnected[slot] = connected;
        }

        var perSlot = new int[n];
        var lastSeq = new int[n];
        var ackConnection = new string?[n];
        var rejections = new List<RejectItem>();
        while (_mailbox.Reader.TryRead(out var item))
        {
            var slot = _slotByMember[item.MemberId];
            var control = controls[slot];
            string? code = null;
            if (State.IsForfeited(slot) || forfeitNow.Contains(slot))
            {
                code = GhostLapFailure.Forfeited;
            }
            else if (control.ConnectionId is null)
            {
                code = GhostLapFailure.NotConnected;
            }
            else if (item.ControllerEpoch != control.Epoch || item.Input.ControllerEpoch != control.Epoch
                     || (item.ConnectionId.Length > 0 && item.ConnectionId != control.ConnectionId))
            {
                code = GhostLapFailure.Superseded;
            }
            else if (perSlot[slot] >= _options.MaxInputsPerSlotPerTick)
            {
                code = GhostLapFailure.TickInputLimit;
            }
            else
            {
                var g = item.Input;
                var admitted = InputAdmission.Admit(world, pending, new SubmittedInput((byte)slot, g.Seq, g.ClientTick, g.MoveX, g.MoveY, g.RequestBurst));
                if (admitted.TryGetValue(out var input))
                {
                    pending.Add(input);
                    perSlot[slot]++;
                    lastSeq[slot] = input.Sequence;
                    ackConnection[slot] = control.ConnectionId;
                }
                else
                {
                    code = Code(admitted.Error);
                }
            }
            if (code is not null)
            {
                TotalRejected++;
                rejections.Add(new RejectItem(item.ConnectionId, slot, item.Input.Seq, code, SequenceFloor(world, pending, slot)));
            }
        }

        State = MpRules.Step(State, pending, forfeitNow, Context);
        var acks = new List<AckItem>();
        for (var slot = 0; slot < n; slot++)
        {
            if (ackConnection[slot] is { } connection)
            {
                acks.Add(new AckItem(connection, slot, controls[slot].Epoch, next, lastSeq[slot]));
            }
        }
        var presence = new SlotPresence[n];
        for (var slot = 0; slot < n; slot++)
        {
            var connected = controls[slot].ConnectionId is not null;
            presence[slot] = new SlotPresence(connected, !connected && !State.IsForfeited(slot) && _graceStart[slot] != 0
                ? _graceStart[slot] + MpRulesProfile.GraceTicks
                : 0);
        }
        return new TickOutcome(next, pending, acks, rejections, neutralized, State.IsFinal, forfeitNow, ResolveEvents(next, forfeitNow), presence);
    }

    /// <summary>The highest sequence applied or pending for a slot: what a controller that lost its counter must exceed.</summary>
    private static int SequenceFloor(GameState world, List<GameInput> pending, int slot)
    {
        var floor = world.Players[slot].LastSequence;
        foreach (var p in pending)
        {
            if (p.Slot == slot && p.Sequence > floor)
            {
                floor = p.Sequence;
            }
        }
        return floor;
    }

    /// <summary>
    /// The tick's resolved events in their stable order: the phase 0 forfeits first (slot order), then the shared ghost-v1 phase events as the
    /// reducer emitted them. The index is the position in this list, so <c>(match, generation, tick, index)</c> never changes meaning.
    /// </summary>
    private List<MatchEvent> ResolveEvents(long tick, List<int> forfeitNow)
    {
        var events = new List<MatchEvent>(forfeitNow.Count + State.World.Events.Length);
        foreach (var slot in forfeitNow)
        {
            events.Add(new MatchEvent(tick, events.Count, MatchEventKinds.Forfeited, slot, 0, State.World.Players[slot].TotalScore, null));
        }
        foreach (var e in State.World.Events)
        {
            events.Add(new MatchEvent(tick, events.Count, KindOf(e.Kind), e.Slot, e.OtherSlot, e.Value, e.CandleId.Length == 0 ? null : e.CandleId));
        }
        if (events.Count >= 1024)
        {
            throw new InvalidOperationException("A tick resolved more events than the event key space allows.");
        }
        return events;
    }

    private static string KindOf(GameEventKind kind) => kind switch
    {
        GameEventKind.CandleCollected => MatchEventKinds.CandleCollected,
        GameEventKind.OverlitStarted => MatchEventKinds.OverlitStarted,
        GameEventKind.ContactDrain => MatchEventKinds.ContactDrain,
        GameEventKind.BurstManual => MatchEventKinds.BurstManual,
        GameEventKind.BurstAuto => MatchEventKinds.BurstAuto,
        GameEventKind.BurstHit => MatchEventKinds.BurstHit,
        GameEventKind.OverlitCancelled => MatchEventKinds.OverlitCancelled,
        GameEventKind.RoundFinished => MatchEventKinds.RoundFinished,
        _ => throw new InvalidOperationException($"Unmapped game event kind {kind}."),
    };

    private static string Code(Simulation.InputRejection rejection) => rejection switch
    {
        Simulation.InputRejection.RoundOver => GhostLapFailure.RoundOver,
        Simulation.InputRejection.UnknownSlot => GhostLapFailure.Forbidden,
        Simulation.InputRejection.InvalidVector => GhostLapFailure.InvalidRequest,
        Simulation.InputRejection.StaleSequence => GhostLapFailure.StaleSequence,
        Simulation.InputRejection.LateTick => GhostLapFailure.LateTick,
        Simulation.InputRejection.FutureTick => GhostLapFailure.FutureTick,
        _ => GhostLapFailure.InvalidRequest,
    };

    /// <summary>One slot's controller, swapped atomically as an immutable value.</summary>
    private sealed class SlotControlCell(int slot)
    {
        private Box _box = new(new SlotControl(slot, -1, null));

        public SlotControl Current => Volatile.Read(ref _box).Value;

        public void Set(long epoch, string? connectionId) => Volatile.Write(ref _box, new Box(new SlotControl(slot, epoch, connectionId)));

        private sealed record Box(SlotControl Value);
    }
}

/// <summary>A scheduler decision for one iteration.</summary>
public readonly record struct SchedulerDecision(int RunNow, long BacklogTicks, bool Overloaded, TimeSpan Wait);

/// <summary>Cumulative scheduler telemetry (drift and catch-up). Observations, not targets.</summary>
public sealed class SchedulerTelemetry
{
    public long Iterations { get; internal set; }

    public long CatchUpIterations { get; internal set; }

    public long LateTicks { get; internal set; }

    public long MaxBacklogTicks { get; internal set; }

    public long MaxStartLatenessMicroseconds { get; internal set; }
}

/// <summary>
/// The 30 Hz monotonic tick scheduler (R10.09). Tick <c>k</c> is due at <c>start + k / 30 s</c> on the server's monotonic clock; the
/// canonical tick count, not wall time, drives the game. When late it runs every due tick in order, at most
/// <see cref="MultiplayerOptions.CatchUpTicksPerIteration"/> per iteration, never skips one and never scales movement by elapsed
/// time. Overload: a backlog above <see cref="MultiplayerOptions.LateAlarmTicks"/> continuously for longer than
/// <see cref="MultiplayerOptions.OverloadSustainedMilliseconds"/>, or any backlog above
/// <see cref="MultiplayerOptions.OverloadMaxBacklogTicks"/>. Pure arithmetic over timestamps passed in, so tests drive it exactly.
/// </summary>
public sealed class TickScheduler
{
    private readonly long _start;
    private readonly long _frequency;
    private readonly MultiplayerOptions _options;
    private long? _lateSince;

    public TickScheduler(long startTimestamp, long timestampFrequency, MultiplayerOptions options)
    {
        if (timestampFrequency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timestampFrequency));
        }
        _start = startTimestamp;
        _frequency = timestampFrequency;
        _options = options;
    }

    public SchedulerTelemetry Telemetry { get; } = new();

    /// <summary>First timestamp at which tick <paramref name="tick"/> is due (rounded up, so <c>DueTicks(DueAt(k)) &gt;= k</c>).</summary>
    public long DueAt(long tick) => _start + (((tick * _frequency) + GameRulesV1.TickRate - 1) / GameRulesV1.TickRate);

    /// <summary>Ticks due by <paramref name="now"/> (capped at the round length).</summary>
    public long DueTicks(long now) => now <= _start ? 0 : Math.Min(GameRulesV1.RoundTicks, (now - _start) * GameRulesV1.TickRate / _frequency);

    public SchedulerDecision Next(long now, long executed)
    {
        Telemetry.Iterations++;
        var backlog = Math.Max(0, DueTicks(now) - executed);
        Telemetry.MaxBacklogTicks = Math.Max(Telemetry.MaxBacklogTicks, backlog);
        var overloaded = backlog > _options.OverloadMaxBacklogTicks;
        if (backlog > _options.LateAlarmTicks)
        {
            _lateSince ??= now;
            if ((now - _lateSince.Value) * 1000 / _frequency > _options.OverloadSustainedMilliseconds)
            {
                overloaded = true;
            }
        }
        else
        {
            _lateSince = null;
        }
        var run = (int)Math.Min(backlog, _options.CatchUpTicksPerIteration);
        if (run > 1)
        {
            Telemetry.CatchUpIterations++;
        }
        var wait = TimeSpan.Zero;
        if (run == 0 && executed < GameRulesV1.RoundTicks)
        {
            var ticks = Math.Max(1, DueAt(executed + 1) - now);
            wait = TimeSpan.FromTicks(((ticks * TimeSpan.TicksPerSecond) + _frequency - 1) / _frequency);
        }
        return new SchedulerDecision(run, backlog, overloaded, wait);
    }

    /// <summary>Records the start lateness of a tick that is about to run.</summary>
    public void Started(long tick, long now)
    {
        var late = now - DueAt(tick);
        if (late * GameRulesV1.TickRate > _frequency)
        {
            Telemetry.LateTicks++;
        }
        Telemetry.MaxStartLatenessMicroseconds = Math.Max(Telemetry.MaxStartLatenessMicroseconds, Math.Max(0, late) * 1_000_000 / _frequency);
    }
}

public enum MatchStopReason
{
    /// <summary>Tick 2700 was resolved.</summary>
    Finished,

    /// <summary>The scheduler could not keep up within the configured bound.</summary>
    Overloaded,

    /// <summary>This process is no longer the authority (guard lost or generation retired).</summary>
    AuthorityLost,

    /// <summary>The host is stopping or the match was cancelled.</summary>
    Cancelled,
}

/// <summary>
/// Drives one <see cref="MatchEngine"/> on the <see cref="TickScheduler"/>. Before every tick it reads the authority flag (a volatile
/// read); once authority is gone it executes and publishes nothing more. Publication is a non-blocking hand-off; the tick never
/// awaits a socket or the database.
/// </summary>
public sealed class MatchDriver(MatchEngine engine, IRealtimeAuthority authority, TimeProvider time, MultiplayerOptions options, Action<TickOutcome> publish)
{
    public TickScheduler? Scheduler { get; private set; }

    /// <param name="wait">How to wait for the next due tick. Production: <c>Task.Delay</c> on the time provider. Tests advance a manual clock.</param>
    public async Task<MatchStopReason> RunAsync(Func<TimeSpan, CancellationToken, Task> wait, CancellationToken ct)
    {
        var scheduler = new TickScheduler(time.GetTimestamp(), time.TimestampFrequency, options);
        Scheduler = scheduler;
        try
        {
            while (!engine.State.IsFinal)
            {
                if (ct.IsCancellationRequested)
                {
                    return Stopped();
                }
                if (!authority.IsValid)
                {
                    return MatchStopReason.AuthorityLost;
                }
                var decision = scheduler.Next(time.GetTimestamp(), engine.State.Tick);
                if (decision.Overloaded)
                {
                    return MatchStopReason.Overloaded;
                }
                if (decision.RunNow == 0)
                {
                    await wait(decision.Wait, ct).ConfigureAwait(false);
                    continue;
                }
                for (var i = 0; i < decision.RunNow && !engine.State.IsFinal; i++)
                {
                    if (!authority.IsValid)
                    {
                        return MatchStopReason.AuthorityLost;
                    }
                    if (ct.IsCancellationRequested)
                    {
                        return Stopped();
                    }
                    scheduler.Started(engine.State.Tick + 1, time.GetTimestamp());
                    publish(engine.Step());
                }
                if (decision.RunNow < decision.BacklogTicks)
                {
                    await Task.Yield(); // bounded catch-up: let other rooms and ingress run between iterations
                }
            }
            return MatchStopReason.Finished;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Stopped();
        }
    }

    // A host that stops BECAUSE it lost authority (guard loss, retirement) reports the cause, not the shutdown that followed it.
    private MatchStopReason Stopped() => authority.IsValid ? MatchStopReason.Cancelled : MatchStopReason.AuthorityLost;
}
