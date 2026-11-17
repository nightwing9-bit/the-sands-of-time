using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using GhostLap.Contracts.Multiplayer;
using GhostLap.Rules;
using GhostLap.Simulation;
using InputRejection = GhostLap.Contracts.Multiplayer.InputRejection;
using Microsoft.AspNetCore.SignalR.Client;
using Tab = Integration.Tests.GhostLap.GhostLapHubTests.Tab;

namespace Integration.Tests.GhostLap;

internal static class Stat
{
    public static double P(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0)
        {
            return double.NaN;
        }
        var sorted = values.Order().ToArray();
        return sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
    }
}

/// <summary>Chooses the next movement of a player from the latest authoritative snapshot only (the client never sees the server's world).</summary>
internal sealed class CandleBrain(CandlePlan plan)
{
    private readonly Dictionary<string, CandleSpec> _byId = plan.Candles.ToDictionary(c => c.Id);
    private (int X, int Y)? _last;
    private int _stuck;
    private int _detour;
    private int _burstSends;
    private bool _overlitHandled;

    /// <summary>Direction and the requested Burst state. The Burst is pressed for three consecutive decisions, once per Overlit.</summary>
    public (sbyte X, sbyte Y, bool Burst) Decide(MatchSnapshot s, int slot, long aimTick)
    {
        var me = s.Players[slot];
        var burst = false;
        if (me.Charge == GameRulesV1.MaxCharge && !_overlitHandled)
        {
            burst = true;
            if (++_burstSends >= 3)
            {
                _overlitHandled = true;
            }
        }
        else if (me.Charge < GameRulesV1.MaxCharge)
        {
            _overlitHandled = false;
            _burstSends = 0;
        }
        var target = s.ActiveCandleIds
            .Select(id => _byId.GetValueOrDefault(id))
            .OfType<CandleSpec>()
            .OrderBy(c => Math.Abs((long)c.Site.X - me.XFixed) + Math.Abs((long)c.Site.Y - me.YFixed))
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        sbyte mx = 0, my = 0;
        if (target is not null)
        {
            const int dead = 12 * GameRulesV1.FixedOne;
            mx = (sbyte)(Math.Abs(target.Site.X - me.XFixed) > dead ? Math.Sign(target.Site.X - me.XFixed) : 0);
            my = (sbyte)(Math.Abs(target.Site.Y - me.YFixed) > dead ? Math.Sign(target.Site.Y - me.YFixed) : 0);
        }
        var moved = _last is not { } l || l != (me.XFixed, me.YFixed);
        _stuck = moved ? 0 : _stuck + 1;
        _last = (me.XFixed, me.YFixed);
        if (_stuck >= 6)
        {
            _detour = 30;
            _stuck = 0;
        }
        if (_detour > 0)
        {
            _detour--;
            (mx, my) = (aimTick / 40 + slot) % 2 == 0 ? ((sbyte)0, (sbyte)1) : ((sbyte)1, (sbyte)-1);
        }
        return (mx, my, burst);
    }
}

internal sealed record NetSummary(
    int Sent,
    int Acked,
    int LateRejects,
    int FutureRejects,
    int OtherRejects,
    double AckP50,
    double AckP95,
    double AckP99,
    int AimErrMin,
    int AimErrMax,
    double AimErrP5,
    double AimErrP95,
    double AgeP50,
    double AgeP95,
    double AgeMax,
    double CorrectionShare,
    double CorrectionP95,
    double CorrectionMax,
    int Frames);

/// <summary>
/// A real game client over a real hub connection: it plays from authoritative snapshots only, aims each input at the server tick it expects the input
/// to be applied on (offset from the last snapshot and the measured acknowledgement latency, exactly the ghost-mp-net-v0 estimate), sends on change
/// plus a keepalive every fourth tick, and measures what the network did to it. Its dead reckoning (own accepted inputs on top of the previous
/// snapshot) is a non-authoritative prediction used only to size the corrections a renderer would need.
/// </summary>
internal sealed class GameClient : IAsyncDisposable
{
    private const double TickMs = 1000.0 / 30;

    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Func<long> _serverTick;
    private readonly List<double> _ackMs = [];
    private readonly List<double> _aimErr = [];
    private readonly List<double> _ageTicks = [];
    private readonly List<double> _corrections = [];
    private readonly Queue<double> _recentAck = new();
    private readonly ConcurrentDictionary<int, SentInput> _sent = new();
    private readonly ConcurrentDictionary<int, byte> _rejectedSeqs = new();
    private readonly ConcurrentDictionary<string, int> _rejectCodes = new();
    private MatchSnapshot? _last;
    private double _lastRecvMs;
    private (long Tick, int X, int Y)? _base;
    private int _acked;
    private double _bias;
    private double _biasUpdatedAtMs;

    internal sealed record SentInput(double AtMs, long Aim, sbyte Mx, sbyte My, bool Burst);

    public GameClient(Tab tab, NetworkConditions net, GhostLapHubTests.GuestLogin login, Func<long> serverTick)
    {
        Tab = tab;
        Net = net;
        Login = login;
        _serverTick = serverTick;
        tab.Connection.On<SnapshotFrame>(GhostLapWire.SnapshotClientMethod, OnFrame);
        tab.Connection.On<InputAck>(GhostLapWire.InputsAppliedClientMethod, OnAck);
        tab.Connection.On<InputRejection>(GhostLapWire.InputRejectedClientMethod, OnReject);
        tab.Connection.On<MatchControlNotice>(GhostLapWire.MatchControlClientMethod, n => Controls.Enqueue(n));
    }

    public Tab Tab { get; }

    public NetworkConditions Net { get; }

    public GhostLapHubTests.GuestLogin Login { get; }

    public JoinedRoom Joined { get; set; } = null!;

    public int Slot { get; set; } = -1;

    public SnapshotFilter Filter { get; } = new();

    public ConcurrentQueue<SnapshotFrame> Frames { get; } = new();

    public ConcurrentQueue<MatchEvent> AppliedEvents { get; } = new();

    public ConcurrentQueue<InputAck> Acks { get; } = new();

    public ConcurrentQueue<MatchControlNotice> Controls { get; } = new();

    public ConcurrentQueue<SnapshotVerdict> Verdicts { get; } = new();

    public Channel<GameplayInput> Inputs { get; } = Channel.CreateUnbounded<GameplayInput>();

    public Task<GhostLapReply<InputStreamSummary>>? Stream { get; private set; }

    public string MatchId { get; set; } = string.Empty;

    public int Seq;

    public int SentCount => Volatile.Read(ref Seq);

    public IReadOnlyDictionary<int, SentInput> Sent => _sent;

    public MatchSnapshot? Last
    {
        get
        {
            lock (_gate)
            {
                return _last;
            }
        }
    }

    public double RttEstimateMs
    {
        get
        {
            lock (_gate)
            {
                return _recentAck.Count == 0 ? 0 : _recentAck.Min();
            }
        }
    }

    public int RejectionCount(string code) => _rejectCodes.GetValueOrDefault(code);

    /// <summary>Sequence numbers of items the server refused (any layer) as far as this client was told.</summary>
    public IReadOnlyCollection<int> RejectedSeqs => [.. _rejectedSeqs.Keys];

    /// <summary>Starts a fresh measurement window (after warm-up). Does not touch sequence numbers, baselines or the filter.</summary>
    public void ResetMetrics()
    {
        lock (_gate)
        {
            _ackMs.Clear();
            _aimErr.Clear();
            _ageTicks.Clear();
            _corrections.Clear();
            _acked = 0;
            _sentAtReset = Volatile.Read(ref Seq);
        }
        _rejectCodes.Clear();
    }

    private int _sentAtReset;

    public IReadOnlyDictionary<string, int> Rejections => _rejectCodes;

    public void OpenStream() => Stream = Tab.StreamAsync(Inputs.Reader);

    private void OnFrame(SnapshotFrame frame)
    {
        var now = _clock.Elapsed.TotalMilliseconds;
        var serverTick = _serverTick();
        var result = Filter.Offer(frame);
        Verdicts.Enqueue(result.Verdict);
        Frames.Enqueue(frame);
        if (!result.Applied)
        {
            return;
        }
        foreach (var e in result.NewEvents)
        {
            AppliedEvents.Enqueue(e);
        }
        lock (_gate)
        {
            _last = frame.Snapshot;
            _lastRecvMs = now;
            _ageTicks.Add(Math.Max(0, serverTick - frame.Snapshot.Tick));
            if (Slot >= 0 && Slot < frame.Snapshot.Players.Count)
            {
                var me = frame.Snapshot.Players[Slot];
                if (_base is { } b && !frame.IsResync && me.Status == SlotStatuses.Active && frame.Snapshot.Tick > b.Tick)
                {
                    var predicted = Predict(b, frame.Snapshot.Tick);
                    _corrections.Add(Math.Max(Math.Abs(predicted.X - me.XFixed), Math.Abs(predicted.Y - me.YFixed)) / (double)GameRulesV1.FixedOne);
                }
                _base = (frame.Snapshot.Tick, me.XFixed, me.YFixed);
            }
        }
    }

    /// <summary>Dead reckoning: this client's own inputs, as it believes the server applied them (aimed tick, six-tick hold), on top of a snapshot.</summary>
    private (int X, int Y) Predict((long Tick, int X, int Y) from, long toTick)
    {
        var inputs = _sent.Where(kv => !_rejectedSeqs.ContainsKey(kv.Key)).Select(kv => (Seq: kv.Key, Info: kv.Value)).ToArray();
        var (x, y) = (from.X, from.Y);
        for (var t = from.Tick + 1; t <= toTick; t++)
        {
            var active = inputs.Where(i => i.Info.Aim <= t && i.Info.Aim >= t - GameRulesV1.InputHoldTicks)
                .OrderByDescending(i => i.Info.Aim).ThenByDescending(i => i.Seq).FirstOrDefault();
            if (active.Info is not null)
            {
                var moved = Movement.Move(x, y, active.Info.Mx, active.Info.My);
                (x, y) = (moved.XFixed, moved.YFixed);
            }
        }
        return (x, y);
    }

    private void OnAck(InputAck ack)
    {
        var now = _clock.Elapsed.TotalMilliseconds;
        Acks.Enqueue(ack);
        if (!_sent.TryGetValue(ack.LastAppliedSeq, out var info))
        {
            return;
        }
        lock (_gate)
        {
            var latency = now - info.AtMs;
            _ackMs.Add(latency);
            _aimErr.Add(info.Aim - ack.Tick);
            _recentAck.Enqueue(latency);
            while (_recentAck.Count > 30)
            {
                _recentAck.Dequeue();
            }
            _acked++;
            UpdateBias(info, ack.Tick, now);
        }
    }

    /// <summary>
    /// Closed-loop correction of the aim (ghost-mp-net-v0 needs it to start at all: the first inputs have no RTT sample, and a refusal is then the only
    /// way to learn the offset, which is why a loop refusal carries the tick that evaluated it). The window is 4 ticks behind to 2 ahead, so the target
    /// error is its centre, -1. A sample counts only if the input was sent after the previous correction, so inputs already in flight cannot be
    /// corrected twice.
    /// </summary>
    private void UpdateBias(SentInput info, long appliedTick, double nowMs)
    {
        if (info.AtMs < _biasUpdatedAtMs)
        {
            return;
        }
        var error = info.Aim - appliedTick;
        if (Math.Abs(error + 1) >= 1)
        {
            _bias = Math.Clamp(_bias - ((error + 1) * 0.5), -20, 20);
            _biasUpdatedAtMs = nowMs;
        }
    }

    private void OnReject(InputRejection rejection)
    {
        _rejectCodes.AddOrUpdate(rejection.Code, 1, (_, n) => n + 1);
        _rejectedSeqs[rejection.Seq] = 0;
        if (rejection.Code is GhostLapFailure.LateTick or GhostLapFailure.FutureTick && rejection.Tick is { } tick && _sent.TryGetValue(rejection.Seq, out var info))
        {
            lock (_gate)
            {
                UpdateBias(info, tick, _clock.Elapsed.TotalMilliseconds);
            }
        }
    }

    /// <summary>The tick the next input should be tagged with: the server tick it will be applied on, estimated from the last snapshot and the RTT.</summary>
    public long AimTick()
    {
        MatchSnapshot? snapshot;
        double recv;
        lock (_gate)
        {
            snapshot = _last;
            recv = _lastRecvMs;
        }
        if (snapshot is null)
        {
            return 1;
        }
        var elapsed = _clock.Elapsed.TotalMilliseconds - recv;
        double bias;
        lock (_gate)
        {
            bias = _bias;
        }
        return snapshot.Tick + (long)Math.Round(((elapsed + RttEstimateMs) / TickMs) + bias) + 1;
    }

    public GameplayInput Send(sbyte mx, sbyte my, bool burst, long? aim = null, int? seq = null)
    {
        var s = seq ?? Interlocked.Increment(ref Seq);
        var tick = aim ?? AimTick();
        var input = new GameplayInput(MatchId, Joined.ControllerEpoch, tick, s, mx, my, burst);
        _sent[s] = new SentInput(_clock.Elapsed.TotalMilliseconds, tick, mx, my, burst);
        Inputs.Writer.TryWrite(input);
        return input;
    }

    /// <summary>Plays at 30 Hz on a fixed schedule until cancelled: sends on change and a keepalive every fourth tick while moving.</summary>
    public async Task PlayAsync(CandleBrain brain, CancellationToken ct)
    {
        var next = _clock.Elapsed.TotalMilliseconds;
        (sbyte X, sbyte Y, bool Burst) lastSent = (0, 0, false);
        var sinceSend = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                next += TickMs;
                var wait = next - _clock.Elapsed.TotalMilliseconds;
                if (wait > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(wait), ct);
                }
                var snapshot = Last;
                if (snapshot is null || Slot < 0 || snapshot.IsFinal || snapshot.Players[Slot].Status == SlotStatuses.Forfeited)
                {
                    continue;
                }
                var aim = AimTick();
                var want = brain.Decide(snapshot, Slot, aim);
                sinceSend++;
                var moving = want.X != 0 || want.Y != 0;
                if (want != lastSent || (moving && sinceSend >= 4))
                {
                    Send(want.X, want.Y, want.Burst, aim);
                    lastSent = want;
                    sinceSend = 0;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public NetSummary Summary()
    {
        lock (_gate)
        {
            return new NetSummary(
                SentCount - _sentAtReset,
                _acked,
                RejectionCount(GhostLapFailure.LateTick),
                RejectionCount(GhostLapFailure.FutureTick),
                _rejectCodes.Where(kv => kv.Key is not (GhostLapFailure.LateTick or GhostLapFailure.FutureTick)).Sum(kv => kv.Value),
                Stat.P(_ackMs, 0.5),
                Stat.P(_ackMs, 0.95),
                Stat.P(_ackMs, 0.99),
                _aimErr.Count == 0 ? 0 : (int)_aimErr.Min(),
                _aimErr.Count == 0 ? 0 : (int)_aimErr.Max(),
                Stat.P(_aimErr, 0.05),
                Stat.P(_aimErr, 0.95),
                Stat.P(_ageTicks, 0.5) * TickMs,
                Stat.P(_ageTicks, 0.95) * TickMs,
                _ageTicks.Count == 0 ? 0 : _ageTicks.Max() * TickMs,
                _corrections.Count == 0 ? 0 : _corrections.Count(c => c > 0) / (double)_corrections.Count,
                Stat.P(_corrections, 0.95),
                _corrections.Count == 0 ? 0 : _corrections.Max(),
                Frames.Count);
        }
    }

    public static string Format(string label, NetSummary s) => string.Create(CultureInfo.InvariantCulture,
        $"{label}: sent={s.Sent} acked={s.Acked} late={s.LateRejects} future={s.FutureRejects} other={s.OtherRejects} | ack ms p50/p95/p99={s.AckP50:F0}/{s.AckP95:F0}/{s.AckP99:F0}"
        + $" | aimErr ticks min/p5/p95/max={s.AimErrMin}/{s.AimErrP5:F0}/{s.AimErrP95:F0}/{s.AimErrMax} | snapshot age ms p50/p95/max={s.AgeP50:F0}/{s.AgeP95:F0}/{s.AgeMax:F0}"
        + $" | correction share={s.CorrectionShare:P1} p95={s.CorrectionP95:F1}u max={s.CorrectionMax:F1}u | frames={s.Frames}");

    public async ValueTask DisposeAsync()
    {
        Inputs.Writer.TryComplete();
        try
        {
            await Tab.DisposeAsync();
        }
        catch (Exception)
        {
        }
    }
}
