using System.Collections.Concurrent;
using System.Threading.Channels;
using GhostLap.Contracts.Multiplayer;

namespace GhostLap.Multiplayer;

/// <summary>
/// One connection that watches or controls a match: the room owner's view of it, given to the match through <see cref="IMatchPresence"/>.
/// <see cref="Slot"/> is -1 for a spectator. <see cref="Controlling"/> is true only for the one connection that holds the slot's current epoch.
/// </summary>
public readonly record struct MatchViewer(string ConnectionId, string MemberId, bool Spectator, int Slot, long Epoch, bool Controlling);

/// <summary>
/// The outbound edge of the fan-out (SignalR in the host, a controllable transport in tests). Every method is awaited by exactly one
/// recipient's own sender task, so a slow connection can only delay itself. <see cref="Abort"/> closes the connection and must not block.
/// </summary>
public interface IMatchTransport
{
    ValueTask SendSnapshotAsync(string connectionId, SnapshotFrame frame, CancellationToken ct);

    ValueTask SendAckAsync(string connectionId, InputAck ack, CancellationToken ct);

    ValueTask SendRejectionAsync(string connectionId, InputRejection rejection, CancellationToken ct);

    ValueTask SendControlAsync(string connectionId, MatchControlNotice notice, CancellationToken ct);

    void Abort(string connectionId);
}

/// <summary>A transport that delivers nothing (compositions without sockets).</summary>
public sealed class NullMatchTransport : IMatchTransport
{
    public ValueTask SendSnapshotAsync(string connectionId, SnapshotFrame frame, CancellationToken ct) => ValueTask.CompletedTask;

    public ValueTask SendAckAsync(string connectionId, InputAck ack, CancellationToken ct) => ValueTask.CompletedTask;

    public ValueTask SendRejectionAsync(string connectionId, InputRejection rejection, CancellationToken ct) => ValueTask.CompletedTask;

    public ValueTask SendControlAsync(string connectionId, MatchControlNotice notice, CancellationToken ct) => ValueTask.CompletedTask;

    public void Abort(string connectionId)
    {
    }
}

/// <summary>Resync entry point for a connection (hub ingress). Returns null when the connection is not a viewer of a live match.</summary>
public interface IMatchResync
{
    SnapshotFrame? Resync(Guid roomId, string connectionId);
}

/// <summary>Per-recipient counters. Observations for tests and for the 10.5 profile, not targets.</summary>
public sealed class RecipientStats
{
    public long SnapshotsSent;
    public long SnapshotsSkipped;
    public long ResyncFrames;
    public long EventsSent;
    public long AcksSent;
    public long AcksCoalesced;
    public long RejectionsSent;
    public long RejectionsDropped;
    public long Failures;
    public long MaxSendMicroseconds;
    public long MaxSnapshotAgeTicks;
    public int MaxControlQueueDepth;
    public bool Stalled;
}

/// <summary>
/// The delivery state of ONE connection of ONE match, with its own sender task (task 10.3). The match loop only writes small immutable values into
/// it and wakes it; it never awaits it.
/// <list type="bullet">
/// <item><b>Snapshots</b> are not queued. The sender reads the feed's latest snapshot when it is ready to send, so everything published meanwhile is
/// coalesced (drop-stale) and the next frame carries the newest state. The events the recipient has not yet received travel with it (per-recipient
/// cursor), so coalescing loses no cue that is still retained; if older ones were cut the frame is a resync baseline and says so.</item>
/// <item><b>Acknowledgements</b> are cumulative per connection (sequence and tick only rise), so a newer one replaces an unsent older one.</item>
/// <item><b>Rejections</b> are reliable and ordered in a bounded queue. Overflow drops the newest and reports the count in one control notice.</item>
/// <item><b>Lifecycle</b> (running, pending finalization, aborted) rides on every frame and forces a frame when it changes, so a terminal status is
/// never coalesced away.</item>
/// <item>A send that exceeds <see cref="MultiplayerOptions.SendTimeoutMilliseconds"/> cuts the recipient (<see cref="IMatchTransport.Abort"/>).
/// Nothing is sent once the process is no longer the authority.</item>
/// </list>
/// </summary>
public sealed class MatchRecipient
{
    private readonly object _gate = new();
    private readonly string _connectionId;
    private readonly MatchFanout _fanout;
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
    });
    private readonly ConcurrentQueue<InputRejection> _rejections = new();
    private readonly CancellationTokenSource _cts = new();
    private MatchViewer _viewer;
    private InputAck? _ack;
    private int _rejectionCount;
    private long _overflowUnreported;
    private bool _baseline = true;
    private long _eventCursor;
    private long _sentSequence = -1;
    private long _sentLifecycle = -1;
    private bool _forfeitNotified;
    private int _closed;

    internal MatchRecipient(MatchFanout fanout, MatchViewer viewer)
    {
        _fanout = fanout;
        _viewer = viewer;
        _connectionId = viewer.ConnectionId;
        Loop = Task.Run(RunAsync);
    }

    public string ConnectionId => _connectionId;

    public MatchViewer Viewer
    {
        get
        {
            lock (_gate)
            {
                return _viewer;
            }
        }
    }

    public RecipientStats Stats { get; } = new();

    public bool IsClosed => Volatile.Read(ref _closed) == 1;

    /// <summary>Rejections queued for this recipient and not yet sent (the bounded outbound control queue).</summary>
    public int QueuedControl => Volatile.Read(ref _rejectionCount);

    internal Task Loop { get; }

    internal void Update(MatchViewer viewer)
    {
        lock (_gate)
        {
            _viewer = viewer;
        }
        Wake();
    }

    /// <summary>Any thread, never blocks.</summary>
    public void Wake() => _wake.Writer.TryWrite(true);

    internal void OfferAck(InputAck ack)
    {
        InputAck? previous;
        lock (_gate)
        {
            previous = _ack;
            if (previous is not null && (previous.ControllerEpoch > ack.ControllerEpoch || previous.Tick > ack.Tick))
            {
                return; // never replace a newer acknowledgement with an older one
            }
            _ack = ack;
        }
        if (previous is not null)
        {
            Interlocked.Increment(ref Stats.AcksCoalesced);
        }
        Wake();
    }

    internal void OfferRejection(InputRejection rejection)
    {
        if (Volatile.Read(ref _rejectionCount) >= _fanout.Options.ControlQueueCapacity)
        {
            Interlocked.Increment(ref Stats.RejectionsDropped);
            Interlocked.Increment(ref _overflowUnreported);
            Wake();
            return;
        }
        var depth = Interlocked.Increment(ref _rejectionCount);
        var previous = Volatile.Read(ref Stats.MaxControlQueueDepth);
        while (depth > previous)
        {
            var observed = Interlocked.CompareExchange(ref Stats.MaxControlQueueDepth, depth, previous);
            if (observed == previous)
            {
                break;
            }
            previous = observed;
        }
        _rejections.Enqueue(rejection);
        Wake();
    }

    /// <summary>
    /// A consistent resync for this connection: the newest published state, the retained event tail and this recipient's view. It becomes the new
    /// baseline of the recipient, so the pushed stream continues from it without repeating cues. Any thread.
    /// </summary>
    internal SnapshotFrame? Resync()
    {
        lock (_gate)
        {
            return BuildFrame(forceBaseline: true, force: true);
        }
    }

    /// <summary>Under <c>_gate</c>. Null when there is nothing newer to say.</summary>
    private SnapshotFrame? BuildFrame(bool forceBaseline, bool force)
    {
        var latest = _fanout.Feed.Latest;
        if (latest is null)
        {
            return null;
        }
        var (lifecycle, status) = _fanout.Lifecycle();
        var snapshot = latest.Snapshot;
        var baseline = forceBaseline || _baseline;
        if (!force && !baseline && snapshot.Sequence <= _sentSequence && lifecycle == _sentLifecycle)
        {
            return null;
        }
        var cursor = baseline ? Math.Max(0, snapshot.Tick - _fanout.Options.EventRetentionTicks) : _eventCursor;
        var slice = _fanout.Feed.EventsAfter(latest, cursor);
        var resync = baseline || slice.Truncated;
        var viewer = _viewer;
        var you = viewer.Spectator || viewer.Slot < 0 || viewer.Slot >= snapshot.Players.Count
            ? new RecipientView(RoomRoles.Spectator, -1, 0, false, -1)
            : new RecipientView(
                RoomRoles.Competitor,
                viewer.Slot,
                viewer.Epoch,
                viewer.Controlling && snapshot.Players[viewer.Slot].Status != SlotStatuses.Forfeited,
                Math.Max(snapshot.Players[viewer.Slot].LastSequence, _fanout.Feed.CurrentFloor(viewer.Slot)));
        if (snapshot.Sequence > _sentSequence + 1 && _sentSequence >= 0)
        {
            Interlocked.Add(ref Stats.SnapshotsSkipped, snapshot.Sequence - _sentSequence - 1);
        }
        Interlocked.Add(ref Stats.EventsSent, slice.Items.Count);
        if (resync)
        {
            Interlocked.Increment(ref Stats.ResyncFrames);
        }
        var age = Math.Max(0, _fanout.Feed.HeadTick - snapshot.Tick);
        if (age > Interlocked.Read(ref Stats.MaxSnapshotAgeTicks))
        {
            Interlocked.Exchange(ref Stats.MaxSnapshotAgeTicks, age);
        }
        _baseline = false;
        _eventCursor = Math.Max(_eventCursor, slice.ThroughTick);
        _sentSequence = Math.Max(_sentSequence, snapshot.Sequence);
        _sentLifecycle = lifecycle;
        return new SnapshotFrame(snapshot, lifecycle, status, you, new EventBatch(slice.FromTick, slice.ThroughTick, slice.Truncated && !baseline, slice.Items), resync);
    }

    private async Task RunAsync()
    {
        var reader = _wake.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                reader.TryRead(out _);
                while (!_cts.IsCancellationRequested)
                {
                    if (!_fanout.Authority.IsValid)
                    {
                        break; // a process that lost authority publishes nothing
                    }
                    if (!await PumpOnceAsync().ConfigureAwait(false))
                    {
                        if (IsClosed)
                        {
                            return;
                        }
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Close(abort: false);
        }
    }

    /// <summary>One priority pass: rejections, overflow notice, acknowledgement, forfeit notice, snapshot. True when something was sent.</summary>
    private async Task<bool> PumpOnceAsync()
    {
        var did = false;
        while (_rejections.TryDequeue(out var rejection))
        {
            Interlocked.Decrement(ref _rejectionCount);
            did = true;
            if (!await SendAsync(ct => _fanout.Transport.SendRejectionAsync(_connectionId, rejection, ct)).ConfigureAwait(false))
            {
                return false;
            }
            Interlocked.Increment(ref Stats.RejectionsSent);
        }
        var dropped = Interlocked.Exchange(ref _overflowUnreported, 0);
        if (dropped > 0)
        {
            did = true;
            var notice = _fanout.Notice(MatchControlCodes.ControlOverflow, dropped.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (!await SendAsync(ct => _fanout.Transport.SendControlAsync(_connectionId, notice, ct)).ConfigureAwait(false))
            {
                return false;
            }
        }
        InputAck? ack;
        lock (_gate)
        {
            ack = _ack;
            _ack = null;
        }
        if (ack is not null)
        {
            did = true;
            if (!await SendAsync(ct => _fanout.Transport.SendAckAsync(_connectionId, ack, ct)).ConfigureAwait(false))
            {
                return false;
            }
            Interlocked.Increment(ref Stats.AcksSent);
        }
        SnapshotFrame? frame;
        lock (_gate)
        {
            frame = BuildFrame(forceBaseline: false, force: false);
        }
        if (frame is not null)
        {
            did = true;
            var me = frame.You;
            if (me.Slot >= 0 && !_forfeitNotified && frame.Snapshot.Players[me.Slot].Status == SlotStatuses.Forfeited)
            {
                _forfeitNotified = true;
                var notice = _fanout.Notice(MatchControlCodes.Forfeited, me.Slot.ToString(System.Globalization.CultureInfo.InvariantCulture), frame.Snapshot.Players[me.Slot].ForfeitTick);
                if (!await SendAsync(ct => _fanout.Transport.SendControlAsync(_connectionId, notice, ct)).ConfigureAwait(false))
                {
                    return false;
                }
            }
            if (!await SendAsync(ct => _fanout.Transport.SendSnapshotAsync(_connectionId, frame, ct)).ConfigureAwait(false))
            {
                return false;
            }
            Interlocked.Increment(ref Stats.SnapshotsSent);
            if (_fanout.Telemetry is { } telemetry && _fanout.Options.MeasureSnapshotBytes)
            {
                // On this recipient's own sender task, after the send: never on the tick and never delaying another recipient.
                telemetry.RecordSnapshotBytes(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(frame, System.Text.Json.JsonSerializerOptions.Web).Length);
            }
        }
        return did;
    }

    /// <summary>Awaits one send within the send timeout. A timeout or failure cuts this recipient only.</summary>
    private async Task<bool> SendAsync(Func<CancellationToken, ValueTask> send)
    {
        var started = _fanout.Time.GetTimestamp();
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            linked.CancelAfter(TimeSpan.FromMilliseconds(_fanout.Options.SendTimeoutMilliseconds));
            await send(linked.Token).AsTask().WaitAsync(TimeSpan.FromMilliseconds(_fanout.Options.SendTimeoutMilliseconds), _fanout.Time, _cts.Token).ConfigureAwait(false);
            var micros = (long)(_fanout.Time.GetElapsedTime(started).TotalMilliseconds * 1000);
            if (micros > Interlocked.Read(ref Stats.MaxSendMicroseconds))
            {
                Interlocked.Exchange(ref Stats.MaxSendMicroseconds, micros);
            }
            return true;
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            return false;
        }
        catch (TimeoutException)
        {
            Stats.Stalled = true;
            Interlocked.Increment(ref Stats.Failures);
            Close(abort: true);
            return false;
        }
        catch (OperationCanceledException)
        {
            // The transport honoured the per-send token before WaitAsync's own timer: the same stall.
            Stats.Stalled = true;
            Interlocked.Increment(ref Stats.Failures);
            Close(abort: true);
            return false;
        }
        catch (Exception)
        {
            Interlocked.Increment(ref Stats.Failures);
            Close(abort: true);
            return false;
        }
    }

    /// <summary>Stops this recipient. With <paramref name="abort"/> the connection is closed too (a stalled or broken client).</summary>
    internal void Close(bool abort)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1)
        {
            return;
        }
        _cts.Cancel();
        _wake.Writer.TryComplete();
        _fanout.Forget(this);
        if (abort)
        {
            _fanout.Transport.Abort(_connectionId);
        }
    }
}

/// <summary>
/// All recipients of one match. The match loop calls only non-blocking methods (<see cref="SignalSnapshot"/>, <see cref="OfferAck"/>,
/// <see cref="OfferRejection"/>); the room owner calls <see cref="Attach"/>/<see cref="Detach"/> under its lock, which only allocate a small object
/// and start a task. Nothing here awaits a socket on the caller's thread.
/// </summary>
public sealed class MatchFanout
{
    private readonly ConcurrentDictionary<string, MatchRecipient> _recipients = new(StringComparer.Ordinal);
    private readonly Func<(long Version, string Status)> _lifecycle;
    private int _stopped;

    public MatchFanout(
        Guid matchId,
        Guid generationId,
        MatchFeed feed,
        IMatchTransport transport,
        IRealtimeAuthority authority,
        MultiplayerOptions options,
        TimeProvider time,
        Func<(long Version, string Status)> lifecycle,
        MatchTelemetry? telemetry = null)
    {
        Telemetry = telemetry;
        MatchId = matchId.ToString("N");
        GenerationId = generationId.ToString("N");
        Feed = feed;
        Transport = transport;
        Authority = authority;
        Options = options;
        Time = time;
        _lifecycle = lifecycle;
    }

    /// <summary>Optional task 10.5 measurements; null when the fan-out is built without a profile.</summary>
    public MatchTelemetry? Telemetry { get; }

    public string MatchId { get; }

    public string GenerationId { get; }

    public MatchFeed Feed { get; }

    public IMatchTransport Transport { get; }

    public IRealtimeAuthority Authority { get; }

    public MultiplayerOptions Options { get; }

    public TimeProvider Time { get; }

    public int RecipientCount => _recipients.Count;

    public (long Version, string Status) Lifecycle() => _lifecycle();

    public IReadOnlyCollection<MatchRecipient> Recipients => [.. _recipients.Values];

    public MatchRecipient? Find(string connectionId) => _recipients.GetValueOrDefault(connectionId);

    internal MatchControlNotice Notice(string code, string? detail, long tick = 0)
    {
        var (version, _) = _lifecycle();
        return new MatchControlNotice(MatchId, GenerationId, version, code, detail, tick == 0 ? Feed.HeadTick : tick);
    }

    /// <summary>Adds a viewer or updates its role (a superseded controller becomes a plain viewer). No-op after <see cref="Stop"/>.</summary>
    public void Attach(MatchViewer viewer)
    {
        if (Volatile.Read(ref _stopped) == 1)
        {
            return;
        }
        var recipient = _recipients.AddOrUpdate(
            viewer.ConnectionId,
            _ => new MatchRecipient(this, viewer),
            (_, existing) =>
            {
                existing.Update(viewer);
                return existing;
            });
        recipient.Wake();
    }

    public void Detach(string connectionId)
    {
        if (_recipients.TryGetValue(connectionId, out var recipient))
        {
            recipient.Close(abort: false);
        }
    }

    internal void Forget(MatchRecipient recipient) => _recipients.TryRemove(new KeyValuePair<string, MatchRecipient>(recipient.ConnectionId, recipient));

    /// <summary>A snapshot or a lifecycle change was published: every recipient reads the latest when it is ready.</summary>
    public void SignalSnapshot()
    {
        foreach (var recipient in _recipients.Values)
        {
            recipient.Wake();
        }
    }

    public void OfferAck(string connectionId, InputAck ack) => _recipients.GetValueOrDefault(connectionId)?.OfferAck(ack);

    public void OfferRejection(string connectionId, InputRejection rejection) => _recipients.GetValueOrDefault(connectionId)?.OfferRejection(rejection);

    public SnapshotFrame? Resync(string connectionId) => _recipients.GetValueOrDefault(connectionId)?.Resync();

    /// <summary>Cuts every recipient and refuses new ones.</summary>
    public void Stop()
    {
        Interlocked.Exchange(ref _stopped, 1);
        foreach (var recipient in _recipients.Values)
        {
            recipient.Close(abort: false);
        }
    }

    /// <summary>Waits for every recipient task (bounded by <paramref name="ct"/>). For orderly host shutdown and tests.</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        var loops = _recipients.Values.Select(r => r.Loop).ToArray();
        Stop();
        try
        {
            await Task.WhenAll(loops).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
