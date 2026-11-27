using GhostLap.Contracts.Multiplayer;
using Npgsql;

namespace GhostLap.Multiplayer;

/// <summary>A live match of this process: engine, loop and current status.</summary>
public sealed class MatchRuntime
{
    private sealed record LifecycleState(long Version, string Status);

    private LifecycleState _lifecycle = new(1, MatchStatuses.Running);

    internal MatchRuntime(SealedRoundConfig config, MatchEngine engine, long admittedAt)
    {
        Config = config;
        Engine = engine;
        AdmittedAt = admittedAt;
    }

    /// <summary>The read side: latest authoritative snapshot and bounded event tail (task 10.3).</summary>
    public MatchFeed Feed { get; internal set; } = null!;

    /// <summary>Task 10.5 measurements of this match (tick time, lateness, backlog, mailbox, snapshot bytes). Observation only.</summary>
    public MatchTelemetry Telemetry { get; } = new();

    /// <summary>
    /// Telemetry hook called on the loop thread with every tick's outcome (applied inputs, rejections, events). For tests and the task 10.5 profile;
    /// the callee must be non-blocking. Nothing in production sets it, and it is never an archive of inputs (no multiplayer input journal).
    /// </summary>
    public Action<TickOutcome>? TickObserver { get; set; }

    /// <summary>Per-recipient bounded delivery (task 10.3).</summary>
    public MatchFanout Fanout { get; internal set; } = null!;

    /// <summary>Rises by one on every status change. Rides on every frame so a client can order lifecycle transitions without comparing status text.</summary>
    public long LifecycleVersion => Volatile.Read(ref _lifecycle).Version;

    public SealedRoundConfig Config { get; }

    public MatchEngine Engine { get; }

    public MatchDriver? Driver { get; internal set; }

    public bool Activated { get; internal set; }

    public long AdmittedAt { get; }

    public long? EndedAt { get; internal set; }

    public string Status => Volatile.Read(ref _lifecycle).Status;

    public MatchStopReason? StopReason { get; internal set; }

    /// <summary>The committed result (task 10.4). Set only after this process wrote or read the ResultCommitted receipt with the same digest.</summary>
    public MatchResultRecord? Result { get; internal set; }

    /// <summary>The last finalizer outcome and how many commit attempts were made (observability; null before the round ended).</summary>
    public FinalizeOutcome? LastFinalizeOutcome { get; internal set; }

    public int FinalizeAttemptsMade { get; internal set; }

    public long PublishedTicks;

    internal CancellationTokenSource Cancellation { get; } = new();

    internal Task? Loop { get; set; }

    internal int AbortStarted;

    /// <summary>A runtime with its engine, feed and fan-out, publishing the tick-0 state. Internal: the registry and the tests are the only creators.</summary>
    internal static MatchRuntime Create(
        SealedRoundConfig config,
        MultiplayerOptions options,
        TimeProvider time,
        IRealtimeAuthority authority,
        IMatchTransport transport,
        TimeProvider? fanoutTime = null)
    {
        var engine = new MatchEngine(config, options, time);
        var runtime = new MatchRuntime(config, engine, time.GetTimestamp());
        var feed = new MatchFeed(config.MatchId, authority.GenerationId, options);
        feed.PublishInitial(engine.State, [.. Enumerable.Repeat(new SlotPresence(true, 0), engine.Context.PlayerCount)]);
        runtime.Feed = feed;
        runtime.Fanout = new MatchFanout(
            config.MatchId, authority.GenerationId, feed, transport, authority, options, fanoutTime ?? time, runtime.Lifecycle, runtime.Telemetry);
        return runtime;
    }

    /// <summary>
    /// Called by the match loop after every tick, on the loop's thread. Records the tick in the feed and offers per-recipient work. Every call is a
    /// small non-blocking write (no socket, no SQL, no await), so a slow recipient cannot slow a tick.
    /// </summary>
    internal void PublishTick(TickOutcome outcome)
    {
        Interlocked.Increment(ref PublishedTicks);
        var published = Feed.Publish(outcome, Engine.State);
        TickObserver?.Invoke(outcome);
        var matchId = Config.MatchId.ToString("N");
        foreach (var ack in outcome.Acks)
        {
            Fanout.OfferAck(ack.ConnectionId, new InputAck(matchId, ack.ControllerEpoch, ack.Tick, ack.LastAppliedSeq));
        }
        OfferRejections(outcome.Rejections, outcome.Tick);
        if (published)
        {
            Fanout.SignalSnapshot();
        }
    }

    internal void CloseAndSettleIngress()
    {
        Engine.CloseIngress();
        OfferRejections(Engine.RefuseUndrainedInputs(), Engine.State.Tick);
    }

    private void OfferRejections(IEnumerable<RejectItem> rejections, long tick)
    {
        foreach (var reject in rejections)
        {
            if (reject.ConnectionId.Length > 0)
            {
                Fanout.OfferRejection(reject.ConnectionId, new InputRejection(
                    reject.Seq, reject.Code, reject.SequenceFloor >= 0 ? reject.SequenceFloor : null, tick));
            }
        }
    }

    /// <summary>Version and status as ONE immutable pair, so a frame can never carry a status that belongs to another version.</summary>
    internal (long Version, string Status) Lifecycle()
    {
        var state = Volatile.Read(ref _lifecycle);
        return (state.Version, state.Status);
    }

    internal void SetStatus(string status)
    {
        // Compare-and-swap: the loop's end, the abort path and the sweep may race, and every real change raises the version exactly once.
        LifecycleState current;
        LifecycleState next;
        do
        {
            current = Volatile.Read(ref _lifecycle);
            if (current.Status == status)
            {
                return;
            }
            next = new LifecycleState(current.Version + 1, status);
        }
        while (!ReferenceEquals(Interlocked.CompareExchange(ref _lifecycle, next, current), current));
        Fanout?.SignalSnapshot(); // a status change forces a frame, so a terminal status is never coalesced away
    }
}

/// <summary>
/// The single owner of every authoritative match of this Realtime process (task 10.2). It is the start-admission handoff (commits the
/// durable start receipt BEFORE it answers Admitted), the gameplay sink (per-room serial mailboxes) and the presence target the
/// room owner updates. One <see cref="MatchDriver"/> loop per match; no second tick authority exists. Publication goes through a
/// per-recipient fan-out (<see cref="MatchFanout"/>): each connection has its own sender, so a slow connection delays only itself.
/// </summary>
public sealed class MatchRegistry : IMatchStartAdmission, IGameplayInputSink, IMatchPresence, IMatchResync
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, MatchRuntime> _byMatch = [];
    private readonly Dictionary<Guid, MatchRuntime> _byRoom = [];
    private readonly HashSet<Guid> _reserved = [];
    private long _startsDeniedForCapacity;
    private readonly MultiplayerOptions _options;
    private readonly TimeProvider _time;
    private readonly IRealtimeAuthority _authority;
    private readonly MatchAuthorityStore _store;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;
    private readonly IMatchTransport _transport;
    private readonly TimeSpan _retainEnded = TimeSpan.FromMinutes(10);

    public MatchRegistry(
        MultiplayerOptions options,
        TimeProvider time,
        IRealtimeAuthority authority,
        MatchAuthorityStore store,
        Func<TimeSpan, CancellationToken, Task>? wait = null,
        IMatchTransport? transport = null)
    {
        _options = options;
        _time = time;
        _authority = authority;
        _store = store;
        _wait = wait ?? ((delay, ct) => Task.Delay(delay, time, ct));
        _transport = transport ?? new NullMatchTransport();
    }

    public IReadOnlyList<MatchRuntime> Matches
    {
        get
        {
            lock (_gate)
            {
                return [.. _byMatch.Values];
            }
        }
    }

    public MatchRuntime? Find(Guid matchId)
    {
        lock (_gate)
        {
            return _byMatch.GetValueOrDefault(matchId);
        }
    }

    // ------------------------------------------------------------ start admission (outside the room lock)

    public async ValueTask<StartAdmissionOutcome> AdmitAsync(SealedRoundConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!_authority.IsValid || config.AuthorityGeneration != _authority.GenerationId)
        {
            return StartAdmissionOutcome.Unavailable;
        }
        var ownsReservation = false;
        lock (_gate)
        {
            if (_byMatch.TryGetValue(config.MatchId, out var known))
            {
                // A retry after a lost acknowledgement: the receipt and runtime exist; the room may activate it now.
                // A match id that ran to its end (or is being decided) can never run again: the room seals the next ordinal.
                return known.Config.ConfigDigest == config.ConfigDigest && !known.Activated && known.Status == MatchStatuses.Running
                    ? StartAdmissionOutcome.Admitted
                    : known.Status is MatchStatuses.Aborted or MatchStatuses.Aborting or MatchStatuses.Unverified
                        or MatchStatuses.Finalized or MatchStatuses.PendingFinalization
                        ? StartAdmissionOutcome.Closed
                        : StartAdmissionOutcome.Unavailable;
            }
            if (_byRoom.TryGetValue(config.RoomId, out var other) && other.EndedAt is null)
            {
                return StartAdmissionOutcome.Unavailable; // one live match per room
            }
            if (_byMatch.Count >= _options.MaxRooms)
            {
                return StartAdmissionOutcome.Unavailable;
            }
            // Task 10.5 capacity gate. Decided BEFORE the receipt commit, so a refused start leaves no receipt, runtime or authority behind (the
            // room returns to its lobby and may retry). Live runtimes plus starts whose receipt is being committed count against the limit, so
            // concurrent starts cannot together exceed it.
            if (!_reserved.Contains(config.MatchId))
            {
                if (LiveCountLocked() + _reserved.Count >= _options.MaxConcurrentMatches)
                {
                    Interlocked.Increment(ref _startsDeniedForCapacity);
                    return StartAdmissionOutcome.Unavailable;
                }
                _reserved.Add(config.MatchId);
                ownsReservation = true;
            }
        }
        // The receipt commit is not cancelled by the room's admission timeout: its outcome is always learned, and a committed receipt
        // always gets a runtime (activated by the room, or aborted as an orphan). The caller stops waiting on ct.
        var commit = CommitAndRegisterAsync(config, ownsReservation);
        return await commit.WaitAsync(ct).ConfigureAwait(false);
    }

    private int LiveCountLocked() => _byMatch.Values.Count(r => r.EndedAt is null);

    private async Task<StartAdmissionOutcome> CommitAndRegisterAsync(SealedRoundConfig config, bool ownsReservation)
    {
        var registered = false;
        try
        {
            var (outcome, _) = await _store.CommitStartReceiptAsync(config, _authority.GenerationId, CancellationToken.None).ConfigureAwait(false);
            switch (outcome)
            {
                case StartReceiptOutcome.Committed or StartReceiptOutcome.AlreadyStarted:
                    lock (_gate)
                    {
                        if (!_byMatch.ContainsKey(config.MatchId))
                        {
                            var runtime = NewRuntime(config);
                            _byMatch[config.MatchId] = runtime;
                            _byRoom[config.RoomId] = runtime;
                        }
                        _reserved.Remove(config.MatchId); // the runtime now counts as live; never counted twice
                        registered = true;
                    }
                    return StartAdmissionOutcome.Admitted;
                case StartReceiptOutcome.Terminal:
                    return StartAdmissionOutcome.Closed;
                default:
                    return StartAdmissionOutcome.Unavailable;
            }
        }
        finally
        {
            if (!registered && ownsReservation)
            {
                lock (_gate)
                {
                    _reserved.Remove(config.MatchId); // a failed or refused commit frees its reservation
                }
            }
        }
    }

    /// <summary>
    /// A snapshot of live load for the task 10.5 profile: per-match series and recipient totals and admission counters (the host adds process GC/CPU). Cheap
    /// and read-only; safe to call from any thread.
    /// </summary>
    public MatchLoadReport CaptureLoad(long connectionsRefusedForCapacity = 0)
    {
        MatchRuntime[] all;
        int live;
        int reserved;
        lock (_gate)
        {
            all = [.. _byMatch.Values];
            live = LiveCountLocked();
            reserved = _reserved.Count;
        }
        var matches = all.Select(Describe).ToArray();
        return new MatchLoadReport(
            new CapacityLoad(live, reserved, _options.MaxConcurrentMatches, Interlocked.Read(ref _startsDeniedForCapacity), connectionsRefusedForCapacity),
            matches);
    }

    private static MatchLoad Describe(MatchRuntime runtime)
    {
        var scheduler = runtime.Driver?.Scheduler?.Telemetry;
        var recipients = runtime.Fanout.Recipients;
        var totals = new RecipientTotals(
            recipients.Count,
            recipients.Sum(r => Interlocked.Read(ref r.Stats.SnapshotsSent)),
            recipients.Sum(r => Interlocked.Read(ref r.Stats.SnapshotsSkipped)),
            recipients.Sum(r => Interlocked.Read(ref r.Stats.ResyncFrames)),
            recipients.Sum(r => Interlocked.Read(ref r.Stats.AcksSent)),
            recipients.Sum(r => Interlocked.Read(ref r.Stats.AcksCoalesced)),
            recipients.Sum(r => Interlocked.Read(ref r.Stats.RejectionsSent)),
            recipients.Sum(r => Interlocked.Read(ref r.Stats.RejectionsDropped)),
            recipients.Sum(r => Interlocked.Read(ref r.Stats.Failures)),
            recipients.Count(r => r.Stats.Stalled),
            recipients.Sum(r => r.QueuedControl),
            recipients.Select(r => Interlocked.Read(ref r.Stats.MaxSendMicroseconds)).DefaultIfEmpty(0).Max(),
            recipients.Select(r => Interlocked.Read(ref r.Stats.MaxSnapshotAgeTicks)).DefaultIfEmpty(0).Max());
        var telemetry = runtime.Telemetry;
        return new MatchLoad(
            runtime.Config.MatchId.ToString("N"),
            runtime.Status,
            runtime.Config.Slots.Count,
            runtime.Engine.State.Tick,
            scheduler?.MaxBacklogTicks ?? 0,
            scheduler?.CatchUpIterations ?? 0,
            scheduler?.LateTicks ?? 0,
            telemetry.TickExecutionMicros.Summarize(),
            telemetry.StartLatenessMicros.Summarize(),
            telemetry.BacklogTicks.Summarize(),
            telemetry.MailboxDepth.Summarize(),
            telemetry.SnapshotBytes.Summarize(),
            telemetry.BytesSent,
            runtime.Engine.TotalAccepted,
            runtime.Engine.TotalRejected,
            runtime.Engine.QueuedInputs,
            totals,
            recipients.Select(r =>
            {
                var viewer = r.Viewer;
                return new RecipientLoad(
                    viewer.MemberId,
                    viewer.Slot,
                    r.QueuedControl,
                    Volatile.Read(ref r.Stats.MaxControlQueueDepth),
                    Interlocked.Read(ref r.Stats.SnapshotsSent),
                    Interlocked.Read(ref r.Stats.SnapshotsSkipped),
                    Interlocked.Read(ref r.Stats.ResyncFrames),
                    Interlocked.Read(ref r.Stats.AcksCoalesced),
                    Interlocked.Read(ref r.Stats.Failures),
                    r.Stats.Stalled);
            }).ToArray());
    }

    /// <summary>Starts refused because the process already ran <see cref="MultiplayerOptions.MaxConcurrentMatches"/> matches (observability).</summary>
    public long StartsDeniedForCapacity => Interlocked.Read(ref _startsDeniedForCapacity);

    /// <summary>Starts whose receipt commit is in flight (observability and tests; zero once every commit settled).</summary>
    public int ReservedStarts
    {
        get
        {
            lock (_gate)
            {
                return _reserved.Count;
            }
        }
    }

    /// <summary>
    /// Telemetry hook: called with each new runtime BEFORE its loop can start, so an observer sees tick 1. For tests and the task 10.5 profile; nothing in
    /// production sets it.
    /// </summary>
    public Action<MatchRuntime>? RuntimeCreated { get; set; }

    private MatchRuntime NewRuntime(SealedRoundConfig config)
    {
        var runtime = MatchRuntime.Create(config, _options, _time, _authority, _transport);
        RuntimeCreated?.Invoke(runtime);
        return runtime;
    }

    // ------------------------------------------------------------ presence (called under the room owner's lock; never blocks)

    public void Activate(Guid matchId, IReadOnlyList<SlotControl> controllers, IReadOnlyList<MatchViewer>? viewers = null)
    {
        lock (_gate)
        {
            if (!_byMatch.TryGetValue(matchId, out var runtime) || runtime.Activated || runtime.AbortStarted != 0
                || runtime.Status != MatchStatuses.Running)
            {
                return;
            }
            foreach (var control in controllers)
            {
                runtime.Engine.SetController(control);
            }
            foreach (var viewer in viewers ?? [])
            {
                runtime.Fanout.Attach(viewer);
            }
            runtime.Activated = true;
            var driver = new MatchDriver(runtime.Engine, _authority, _time, _options, runtime.PublishTick, runtime.Telemetry);
            runtime.Driver = driver;
            runtime.Loop = Task.Run(() => RunAsync(runtime, driver));
        }
    }

    public void ControllerChanged(Guid matchId, SlotControl control)
    {
        MatchRuntime? runtime;
        lock (_gate)
        {
            runtime = _byMatch.GetValueOrDefault(matchId);
        }
        runtime?.Engine.SetController(control);
    }

    public void ViewerAttached(Guid matchId, MatchViewer viewer)
    {
        MatchRuntime? runtime;
        lock (_gate)
        {
            runtime = _byMatch.GetValueOrDefault(matchId);
        }
        if (runtime is { Activated: true })
        {
            runtime.Fanout.Attach(viewer);
        }
    }

    public void ViewerDetached(Guid matchId, string connectionId)
    {
        MatchRuntime? runtime;
        lock (_gate)
        {
            runtime = _byMatch.GetValueOrDefault(matchId);
        }
        runtime?.Fanout.Detach(connectionId);
    }

    /// <summary>
    /// A consistent resync for a connection of a live match: the newest published state, the retained event tail and the connection's own view, in
    /// one frame (state and event boundary are the same tick). Also the new baseline of that connection's pushed stream.
    /// </summary>
    public SnapshotFrame? Resync(Guid roomId, string connectionId)
    {
        MatchRuntime? runtime;
        lock (_gate)
        {
            runtime = _byRoom.GetValueOrDefault(roomId);
        }
        return runtime is { Activated: true } && _authority.IsValid ? runtime.Fanout.Resync(connectionId) : null;
    }

    public void Orphaned(Guid matchId)
    {
        MatchRuntime? runtime;
        lock (_gate)
        {
            runtime = _byMatch.GetValueOrDefault(matchId);
        }
        if (runtime is { Activated: false })
        {
            _ = AbortAsync(runtime, AbortReasons.NotActivated, CancellationToken.None);
        }
    }

    public string? StatusOf(Guid matchId)
    {
        lock (_gate)
        {
            return _byMatch.TryGetValue(matchId, out var runtime) ? runtime.Status : null;
        }
    }

    // ------------------------------------------------------------ gameplay sink (socket reader threads)

    public string? Submit(in AdmittedInput input)
    {
        MatchRuntime? runtime;
        lock (_gate)
        {
            runtime = _byRoom.GetValueOrDefault(input.RoomId);
        }
        if (runtime is null || !runtime.Activated || runtime.Status != MatchStatuses.Running)
        {
            return GhostLapFailure.NoRunningMatch;
        }
        if (!_authority.IsValid)
        {
            return GhostLapFailure.Unavailable;
        }
        return runtime.Engine.TryEnqueue(input);
    }

    // ------------------------------------------------------------ housekeeping and shutdown

    /// <summary>Aborts admitted matches the room never activated (orphaned receipts) and forgets ended matches after a while.</summary>
    public void Sweep()
    {
        var now = _time.GetTimestamp();
        List<MatchRuntime> orphans = [];
        lock (_gate)
        {
            foreach (var runtime in _byMatch.Values.ToArray())
            {
                if (!runtime.Activated && runtime.EndedAt is null
                    && _time.GetElapsedTime(runtime.AdmittedAt, now) > TimeSpan.FromMilliseconds(_options.ActivationTimeoutMilliseconds))
                {
                    orphans.Add(runtime);
                }
                if (runtime.EndedAt is { } ended && _time.GetElapsedTime(ended, now) > _retainEnded)
                {
                    runtime.Fanout.Stop();
                    _byMatch.Remove(runtime.Config.MatchId);
                    if (_byRoom.TryGetValue(runtime.Config.RoomId, out var current) && current == runtime)
                    {
                        _byRoom.Remove(runtime.Config.RoomId);
                    }
                }
            }
        }
        foreach (var orphan in orphans)
        {
            _ = AbortAsync(orphan, AbortReasons.NotActivated, CancellationToken.None);
        }
    }

    /// <summary>Host stop: cancel every loop and wait (bounded by <paramref name="ct"/>) for loops and their abort writes.</summary>
    public async Task StopAllAsync(CancellationToken ct)
    {
        MatchRuntime[] all;
        lock (_gate)
        {
            all = [.. _byMatch.Values];
        }
        foreach (var runtime in all)
        {
            runtime.Engine.CloseIngress();
            await runtime.Cancellation.CancelAsync().ConfigureAwait(false);
        }
        var loops = all.Select(r => r.Loop).OfType<Task>().ToArray();
        try
        {
            await Task.WhenAll(loops).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        foreach (var runtime in all)
        {
            runtime.Fanout.Stop();
        }
    }

    private async Task RunAsync(MatchRuntime runtime, MatchDriver driver)
    {
        MatchStopReason reason;
        try
        {
            reason = await driver.RunAsync(_wait, runtime.Cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A reducer or engine fault is not a player outcome: stop and abort, never invent a result.
            reason = MatchStopReason.Overloaded;
        }
        runtime.CloseAndSettleIngress();
        runtime.StopReason = reason;
        if (reason == MatchStopReason.Finished)
        {
            // The final tick is resolved and the loop has ended: no tick waits on this. Until the receipt is ResultCommitted the match is only
            // pending (provisional); nothing is rated or published as final.
            runtime.SetStatus(MatchStatuses.PendingFinalization);
            await FinalizeAsync(runtime).ConfigureAwait(false);
            runtime.EndedAt = _time.GetTimestamp();
            return;
        }
        var abortReason = reason switch
        {
            MatchStopReason.Overloaded => AbortReasons.Overload,
            MatchStopReason.AuthorityLost => AbortReasons.AuthorityLost,
            _ => AbortReasons.HostStopping,
        };
        await AbortAsync(runtime, abortReason, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Task 10.4 finalizer, run after the loop ended (never inside a tick). Builds the result from the resolved final state and commits it with
    /// bounded retries and backoff while this process is the authority. Reported statuses: <c>finalized</c> only after a ResultCommitted receipt
    /// with this digest was written or read; <c>aborted</c> only after an Aborted receipt was read; otherwise <c>unverified</c> (provisional,
    /// unrated). An uncommitted result is never presented as a durable abort, and a lost commit acknowledgement is resolved by the next attempt
    /// (or the final read) finding the committed receipt.
    /// </summary>
    private async Task FinalizeAsync(MatchRuntime runtime)
    {
        MatchResultRecord record;
        try
        {
            record = MatchResults.Build(runtime.Config, runtime.Engine.State, runtime.Engine.Stats);
        }
        catch (Exception)
        {
            runtime.SetStatus(MatchStatuses.Unverified); // cannot happen for a resolved final tick; never invent a result
            return;
        }
        if (!_store.CanFinalize)
        {
            runtime.SetStatus(MatchStatuses.Unverified);
            return;
        }
        var delay = TimeSpan.FromMilliseconds(_options.FinalizeInitialBackoffMilliseconds);
        var max = TimeSpan.FromMilliseconds(_options.FinalizeMaxBackoffMilliseconds);
        for (var attempt = 0; attempt < _options.FinalizeAttempts; attempt++)
        {
            if (!_authority.IsValid)
            {
                break; // authority lost (guard or retirement): no further commit attempt; the database decides
            }
            var (outcome, _) = await _store.FinalizeAsync(record, _authority.GenerationId, _time, CancellationToken.None).ConfigureAwait(false);
            runtime.LastFinalizeOutcome = outcome;
            runtime.FinalizeAttemptsMade = attempt + 1;
            switch (outcome)
            {
                case FinalizeOutcome.Committed or FinalizeOutcome.AlreadyCommitted:
                    runtime.Result = record;
                    runtime.SetStatus(MatchStatuses.Finalized);
                    return;
                case FinalizeOutcome.Aborted:
                    runtime.SetStatus(MatchStatuses.Aborted);
                    return;
                case FinalizeOutcome.Unavailable:
                    break;
                default:
                    await SettleFromDatabaseAsync(runtime, record).ConfigureAwait(false); // NotAuthority / NotFound / Conflict
                    return;
            }
            if (attempt + 1 == _options.FinalizeAttempts || runtime.Cancellation.IsCancellationRequested)
            {
                break;
            }
            try
            {
                await Task.Delay(delay, _time, runtime.Cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break; // host stopping: settle with what the database says now
            }
            delay = delay * 2 > max ? max : delay * 2;
        }
        await SettleFromDatabaseAsync(runtime, record).ConfigureAwait(false);
    }

    /// <summary>Reports the receipt's durable decision when this process cannot (or may no longer) commit. A read failure is <c>unverified</c>.</summary>
    private async Task SettleFromDatabaseAsync(MatchRuntime runtime, MatchResultRecord record)
    {
        var status = MatchStatuses.Unverified;
        try
        {
            var outcome = await _store.FindOutcomeAsync(runtime.Config.MatchId, CancellationToken.None).ConfigureAwait(false);
            if (outcome?.Receipt.TerminalDecision == TerminalDecisions.ResultCommitted && outcome.ResultDigest == record.Digest)
            {
                runtime.Result = record;
                status = MatchStatuses.Finalized;
            }
            else if (outcome?.Receipt.TerminalDecision == TerminalDecisions.Aborted)
            {
                status = MatchStatuses.Aborted;
            }
        }
        catch (Exception e) when (e is NpgsqlException or TimeoutException or InvalidOperationException)
        {
        }
        runtime.SetStatus(status);
    }

    /// <summary>Writes the fenced abort with bounded retries. Only a durable Aborted decision is reported as aborted.</summary>
    private async Task AbortAsync(MatchRuntime runtime, string reason, CancellationToken ct)
    {
        lock (_gate)
        {
            if ((reason == AbortReasons.NotActivated && runtime.Activated) || runtime.AbortStarted != 0)
            {
                return;
            }
            runtime.AbortStarted = 1;
            runtime.Engine.CloseIngress();
        }
        runtime.SetStatus(MatchStatuses.Aborting);
        var status = MatchStatuses.Unverified;
        var delay = TimeSpan.FromMilliseconds(100);
        for (var attempt = 0; attempt < _options.AbortAttempts; attempt++)
        {
            var (outcome, receipt) = await _store.AbortAsync(runtime.Config.MatchId, _authority.GenerationId, reason, ct).ConfigureAwait(false);
            if (outcome == AbortOutcome.Aborted
                || (outcome == AbortOutcome.AlreadyTerminal && receipt?.TerminalDecision == TerminalDecisions.Aborted))
            {
                status = MatchStatuses.Aborted;
                break;
            }
            if (outcome != AbortOutcome.Unavailable)
            {
                break; // NotAuthority / NotFound / a non-abort decision: this process cannot verify an abort
            }
            try
            {
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            delay *= 2;
        }
        runtime.SetStatus(status);
        runtime.EndedAt = _time.GetTimestamp();
    }

    /// <summary>
    /// A rejection decided at ingress (hub or room owner) for a connection that is a recipient of a live match. Returns false when the connection
    /// has no recipient (lobby), so the caller may answer directly.
    /// </summary>
    public bool TryOfferIngressRejection(Guid roomId, string connectionId, InputRejection rejection)
    {
        MatchRuntime? runtime;
        lock (_gate)
        {
            runtime = _byRoom.GetValueOrDefault(roomId);
        }
        if (runtime?.Fanout.Find(connectionId) is not { } recipient)
        {
            return false;
        }
        recipient.OfferRejection(rejection);
        return true;
    }
}
