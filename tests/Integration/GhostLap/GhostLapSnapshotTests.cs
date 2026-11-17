extern alias realtime;
using System.Collections.Concurrent;
using System.Diagnostics;
using GhostLap.Contracts.Multiplayer;
using GhostLap.Multiplayer;
using GhostLap.Rules;
using GhostLap.Simulation;
using Integration.Tests.Support;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using realtime::GhostLapHost;
using Xunit;
using Xunit.Abstractions;

namespace Integration.Tests.GhostLap;

/// <summary>
/// Task 10.3 over the REAL Hosts.Realtime composition (real hub, real guest cookies from the real Hosts.Api, real PostgreSQL receipt): authoritative
/// snapshots, resync, per-recipient delivery, controller handoff, grace and forfeit, and the network measurements. The clients are real SignalR
/// connections whose socket is wrapped by <see cref="ImpairedWebSocket"/>, which changes only what the client's transport observes.
/// <para>
/// What this is NOT: the in-memory test server has no TCP stack, so these runs prove ordering, delay, stall and disconnect handling under injected
/// conditions, not real packet loss, real kernel buffers or a real browser (task 10.6 owns those).
/// </para>
/// </summary>
[Collection(IdentityCollection.Name)]
[Trait("Category", "Integration")]
public sealed class GhostLapSnapshotTests(IdentityDatabase db, ITestOutputHelper output)
{
    private readonly GhostLapHubTests _kit = new(db);

    private static Dictionary<string, string?> Settings(params (string Key, string Value)[] extra)
    {
        var settings = new Dictionary<string, string?> { ["GhostLap:Multiplayer:CountdownMilliseconds"] = "1000" };
        foreach (var (key, value) in extra)
        {
            settings["GhostLap:Multiplayer:" + key] = value;
        }
        return settings;
    }

    private sealed class Session : IAsyncDisposable
    {
        private readonly CancellationTokenSource _play = new();
        private readonly List<Task> _tasks = [];

        public required GhostLapHubTests.Stack Stack { get; init; }

        public required List<GameClient> Players { get; init; }

        public required List<GameClient> Spectators { get; init; }

        public required RoomView View { get; init; }

        public required MatchRuntime Runtime { get; init; }

        public required string RoomCode { get; init; }

        public required ConcurrentQueue<TickOutcome> Outcomes { get; init; }

        public MatchRegistry Registry => Stack.Hub.Services.GetRequiredService<MatchRegistry>();

        public GhostLapRealtimeAuthority Authority => Stack.Hub.Services.GetRequiredService<GhostLapRealtimeAuthority>();

        public MatchEngine Engine => Runtime.Engine;

        public CancellationToken PlayToken => _play.Token;

        public void StartPlaying(params GameClient[] clients)
        {
            var plan = CandlePlan.Create(long.Parse(View.Round!.Seed));
            foreach (var client in clients)
            {
                _tasks.Add(client.PlayAsync(new CandleBrain(plan), _play.Token));
            }
        }

        public async Task StopPlayingAsync()
        {
            await _play.CancelAsync();
            await Task.WhenAll(_tasks);
        }

        public async ValueTask DisposeAsync()
        {
            await _play.CancelAsync();
            try
            {
                await Task.WhenAll(_tasks).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
            }
            foreach (var c in Players.Concat(Spectators))
            {
                await c.DisposeAsync();
            }
            await Stack.DisposeAsync();
        }
    }

    private async Task<Session> StartAsync(
        int players,
        Dictionary<string, string?>? settings = null,
        Func<int, NetworkConditions>? conditions = null,
        int spectators = 0,
        Action<HubConnection>? configure = null)
    {
        var stack = _kit.NewStack(settings ?? Settings());
        // The client contract of the Ghost Lap hub: ping at most every HubKeepAliveSeconds (5 s by default). A client left on the library default of
        // 15 s would be closed by the server's 15 s client timeout while idle (a spectator, a lobby tab).
        configure ??= c => c.KeepAliveInterval = TimeSpan.FromSeconds(5);
        var registry = stack.Hub.Services.GetRequiredService<MatchRegistry>();
        var outcomes = new ConcurrentQueue<TickOutcome>();
        registry.RuntimeCreated = runtime => runtime.TickObserver = outcomes.Enqueue;
        MatchRuntime? runtime = null;
        long ServerTick() => runtime?.Engine.State.Tick ?? 0;
        async Task<GameClient> Open(int index, string name)
        {
            var net = conditions?.Invoke(index) ?? new NetworkConditions();
            var guest = await GhostLapHubTests.GuestAsync(stack);
            var tab = await GhostLapHubTests.OpenAsync(stack, guest, wrapSocket: ws => new ImpairedWebSocket(ws, net, 1000 + index), configure: configure);
            return new GameClient(tab, net, guest, ServerTick);
        }

        var clients = new List<GameClient>();
        for (var i = 0; i < players; i++)
        {
            clients.Add(await Open(i, "p"));
        }
        var created = (await clients[0].Tab.CreateAsync()).Value!;
        clients[0].Joined = created;
        for (var i = 1; i < players; i++)
        {
            var joined = await clients[i].Tab.JoinAsync(created.RoomCode);
            Assert.True(joined.Ok, joined.Code);
            clients[i].Joined = joined.Value!;
        }
        var watchers = new List<GameClient>();
        for (var i = 0; i < spectators; i++)
        {
            var watcher = await Open(100 + i, "s");
            var joined = await watcher.Tab.JoinAsync(created.RoomCode, spectator: true);
            Assert.True(joined.Ok, joined.Code);
            watcher.Joined = joined.Value!;
            watchers.Add(watcher);
        }
        foreach (var c in clients)
        {
            Assert.True((await c.Tab.ReadyAsync(created.View.RoomId)).Ok);
        }
        Assert.True((await clients[0].Tab.StartAsync(created.View.RoomId)).Ok);
        var until = DateTime.UtcNow.AddSeconds(20);
        RoomView? view = null;
        while (DateTime.UtcNow < until && view is null)
        {
            var reply = await clients[0].Tab.GetAsync(created.View.RoomId);
            if (reply.Ok && reply.Value!.MatchStatus == MatchStatuses.Running)
            {
                view = reply.Value;
            }
            else
            {
                await Task.Delay(250);
            }
        }
        Assert.NotNull(view);
        runtime = registry.Find(Guid.Parse(view.Round!.MatchId))!;
        foreach (var c in clients)
        {
            c.MatchId = view.Round.MatchId;
            c.Slot = view.Round.Slots.Single(s => s.MemberId == c.Joined.MemberId).Slot;
            c.OpenStream();
        }
        return new Session
        {
            Stack = stack, Players = clients, Spectators = watchers, View = view, Runtime = runtime, RoomCode = created.RoomCode, Outcomes = outcomes,
        };
    }

    private static async Task Eventually(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException(what);
            }
            await Task.Delay(20);
        }
    }

    private static async Task<GhostLapReply<SnapshotFrame>> ResyncAsync(GameClient c) =>
        await c.Tab.Connection.InvokeAsync<GhostLapReply<SnapshotFrame>>(GhostLapWire.ResyncMethod);

    private static NetworkConditions Net(int oneWayMs, int jitterMs = 0, double spike = 0, int spikeMs = 0) =>
        new() { OneWayMs = oneWayMs, JitterMs = jitterMs, SpikeProbability = spike, SpikeMs = spikeMs };

    // ------------------------------------------------------------------ the full round

    /// <summary>
    /// **Validates: Requirements 10.04, 10.05, 10.07, 10.08, 10.09, 10.10** One full 2700-tick match over genuine client-to-server SignalR streams and
    /// authoritative snapshot delivery: four competitors and a spectator, 50 ms RTT with jitter. The recorded applied inputs replay to the same hash;
    /// every input a client sent is accounted for exactly once (applied or typed-rejected, none lost or duplicated); every admitted Burst press fires
    /// once; each client applies every event exactly once and ends on the final snapshot; no frame of another generation or an older lifecycle exists;
    /// the mailbox and the scheduler backlog stay bounded.
    /// </summary>
    [Fact]
    public async Task A_full_2700_tick_match_runs_over_real_streams_with_exact_delivery_and_no_backlog()
    {
        await using var s = await StartAsync(4, conditions: i => Net(25, 5, 0.01, 60), spectators: 1);
        var sampler = Task.Run(async () =>
        {
            var max = 0;
            while (!s.Engine.State.IsFinal)
            {
                max = Math.Max(max, s.Engine.QueuedInputs);
                await Task.Delay(20);
            }
            return max;
        });
        var clock = Stopwatch.StartNew();
        s.StartPlaying([.. s.Players]);
        await Eventually(() => s.Engine.State.IsFinal, "the round resolves tick 2700", 150_000);
        var elapsed = clock.Elapsed.TotalSeconds;
        await s.StopPlayingAsync();
        var everyone = s.Players.Concat(s.Spectators).ToArray();
        await Eventually(() => everyone.All(c => c.Last?.IsFinal == true), "every client receives the final snapshot", 20_000);
        await Eventually(() => s.Runtime.Status == MatchStatuses.PendingFinalization, "the loop reports the end of the round");
        await Eventually(() => everyone.All(c => c.Frames.Last().MatchStatus == MatchStatuses.PendingFinalization), "every client sees the lifecycle change", 10_000);
        await Task.Delay(1500); // inputs sent just before the end are still in flight; they are refused with a typed code

        // 1. Authority and timing.
        Assert.Equal(GameRulesV1.RoundTicks, s.Engine.State.Tick);
        Assert.Equal(MatchStopReason.Finished, s.Runtime.StopReason);
        Assert.InRange(elapsed, 80, 100); // 2700 ticks at 30 Hz is 90 s of real time (the loop started a moment before this clock)
        var telemetry = s.Runtime.Driver!.Scheduler!.Telemetry;
        var maxMailbox = await sampler;
        Assert.True(maxMailbox <= 64, $"mailbox grew to {maxMailbox}");
        Assert.True(telemetry.MaxBacklogTicks <= 3, $"scheduler backlog reached {telemetry.MaxBacklogTicks} ticks");

        // 2. Determinism: the applied inputs the loop recorded replay to the same final state.
        var outcomes = s.Outcomes.OrderBy(o => o.Tick).ToArray();
        Assert.Equal(Enumerable.Range(1, 2700).Select(i => (long)i), outcomes.Select(o => o.Tick));
        var context = s.Engine.Context;
        var replay = MpMatchState.Initial(context);
        var burstsChecked = 0;
        foreach (var o in outcomes)
        {
            var armed = Enumerable.Range(0, 4).Where(slot => replay.World.Players[slot].IsOverlit && !replay.World.Players[slot].BurstLatch
                                                             && o.Applied.Any(g => g.Slot == slot && g.RequestBurst)).ToArray();
            replay = MpRules.Step(replay, o.Applied, o.Forfeited, context);
            foreach (var slot in Enumerable.Range(0, 4))
            {
                var manual = replay.World.Events.Count(e => e is { Kind: GameEventKind.BurstManual } && e.Slot == slot);
                Assert.True(manual <= 1, $"tick {o.Tick}: {manual} manual Bursts for slot {slot}"); // one-shot: never repeated
                if (manual == 1)
                {
                    Assert.Contains(o.Applied, g => g.Slot == slot && g.RequestBurst);
                }
                if (armed.Contains(slot))
                {
                    // An admitted press on an armed slot is never lost: it fires once, unless another slot's Burst took the charge away first
                    // in the same tick (slot order, ADR-P1-22 D4), which the reducer reports as OverlitCancelled.
                    Assert.True(manual == 1 || replay.World.Events.Any(e => e.Kind == GameEventKind.OverlitCancelled && e.Slot == slot), $"tick {o.Tick}: armed press on slot {slot} lost");
                    burstsChecked++;
                }
            }
        }
        Assert.Equal(MpStateCodec.Hash(s.Engine.State), MpStateCodec.Hash(replay));

        // 3. Conservation: every input a client sent has exactly one fate.
        foreach (var c in s.Players)
        {
            var applied = outcomes.SelectMany(o => o.Applied).Where(g => g.Slot == c.Slot).Select(g => g.Sequence).ToArray();
            var refused = outcomes.SelectMany(o => o.Rejections).Where(r => r.Slot == c.Slot).Select(r => r.Seq).ToArray();
            Assert.Equal(applied.Length, applied.Distinct().Count());
            Assert.Empty(applied.Intersect(refused));
            var ingress = c.RejectedSeqs.Except(refused).ToArray(); // refused before the loop (only after the round ended in a healthy run)
            Assert.Equal(Enumerable.Range(1, c.SentCount), applied.Concat(refused).Concat(ingress).Distinct().Order());
            Assert.All(c.Rejections.Keys, code => Assert.Contains(code, new[] { GhostLapFailure.NoRunningMatch, GhostLapFailure.RoundOver, GhostLapFailure.LateTick, GhostLapFailure.FutureTick }));
        }

        // 4. Delivery: exact, ordered, single-generation, final.
        var oracle = outcomes.SelectMany(o => o.Events).Select(e => e.Key).ToArray();
        var generation = s.Authority.GenerationId.ToString("N");
        foreach (var c in everyone)
        {
            Assert.All(c.Verdicts, v => Assert.True(v is SnapshotVerdict.Baseline or SnapshotVerdict.Accepted or SnapshotVerdict.Stale, v.ToString()));
            Assert.Equal(oracle, c.AppliedEvents.Select(e => e.Key));
            Assert.All(c.Frames, f => Assert.Equal(generation, f.Snapshot.GenerationId));
            var lifecycles = c.Frames.Select(f => f.LifecycleVersion).ToArray();
            Assert.Equal(lifecycles.Order(), lifecycles);
            var last = c.Last!;
            Assert.Equal((2700L, true), (last.Tick, last.IsFinal));
            Assert.Equal(s.Engine.State.World.Players.Select(p => p.TotalScore), last.Players.Select(p => p.TotalScore));
            Assert.Equal(MatchStatuses.PendingFinalization, c.Frames.Last(f => f.Snapshot.IsFinal).MatchStatus);
        }
        output.WriteLine($"FULL-MATCH ticks=2700 seconds={elapsed:F1} maxMailbox={maxMailbox} schedulerMaxBacklog={telemetry.MaxBacklogTicks} lateTicks={telemetry.LateTicks} " +
                         $"maxStartLatenessMs={telemetry.MaxStartLatenessMicroseconds / 1000.0:F2} events={oracle.Length} armedBurstPresses={burstsChecked} hash={MpStateCodec.Hash(s.Engine.State)[..16]}");
        foreach (var c in s.Players)
        {
            output.WriteLine(GameClient.Format($"slot{c.Slot}", c.Summary()));
        }
        foreach (var r in s.Runtime.Fanout.Recipients)
        {
            output.WriteLine($"recipient {r.ConnectionId[..6]} sent={r.Stats.SnapshotsSent} skipped={r.Stats.SnapshotsSkipped} resyncFrames={r.Stats.ResyncFrames} maxSendMs={r.Stats.MaxSendMicroseconds / 1000.0:F1} failures={r.Stats.Failures}");
            Assert.Equal(0, r.Stats.Failures);
        }
    }

    // ------------------------------------------------------------------ network quality

    /// <summary>
    /// **Validates: Requirements 10.03, 10.08, 10.10** Input acceptance, input-to-applied delay, acknowledgement latency, snapshot age and correction
    /// size at 50, 150 and 300 ms RTT with jitter and head-of-line spikes. The numbers are the evidence for reviewing the ghost-mp-net-v0 window
    /// (clientTick accepted from 4 ticks behind to 2 ahead); the assertions are only invariants (nothing duplicated, nothing foreign, nothing grew).
    /// </summary>
    [Theory]
    [InlineData(50, 8)]
    [InlineData(150, 20)]
    [InlineData(300, 40)]
    public async Task Input_and_snapshot_quality_is_measured_at_50_150_and_300_ms_rtt_with_jitter(int rttMs, int jitterMs)
    {
        await using var s = await StartAsync(2, conditions: _ => Net(rttMs / 2, jitterMs, 0.02, 120));
        s.StartPlaying([.. s.Players]);
        await Task.Delay(4_000); // warm-up: the RTT estimate converges
        foreach (var c in s.Players)
        {
            c.ResetMetrics();
        }
        var from = s.Engine.State.Tick;
        await Task.Delay(25_000);
        var to = s.Engine.State.Tick;
        await s.StopPlayingAsync();
        Assert.InRange(to - from, 740, 760);
        foreach (var c in s.Players)
        {
            var sum = c.Summary();
            output.WriteLine(GameClient.Format($"RTT {rttMs}ms jitter ±{jitterMs}ms one-way, slot {c.Slot}", sum));
            Assert.True(sum.Acked > 50, $"acked {sum.Acked}");
            Assert.True(sum.AckP50 >= rttMs * 0.8, $"impairment not applied (ack p50 {sum.AckP50:F0} ms)");
            Assert.Equal(0, sum.OtherRejects);
            Assert.DoesNotContain(c.Verdicts, v => v is SnapshotVerdict.ForeignGeneration or SnapshotVerdict.ForeignMatch);
            Assert.Equal(c.AppliedEvents.Select(e => e.Key).Distinct().Count(), c.AppliedEvents.Count);
        }
        var recipients = s.Runtime.Fanout.Recipients;
        Assert.All(recipients, r => Assert.Equal(0, r.Stats.Failures));
        Assert.True(s.Engine.TotalRejected < s.Players.Sum(p => p.SentCount) / 2);
        output.WriteLine($"RTT {rttMs}: server rejected total={s.Engine.TotalRejected} of sent={s.Players.Sum(p => p.SentCount)}; scheduler maxBacklog={s.Runtime.Driver!.Scheduler!.Telemetry.MaxBacklogTicks}");
    }

    // ------------------------------------------------------------------ controller handoff and resync

    /// <summary>
    /// **Validates: Requirements 10.03, 10.06, 10.08, 10.17** A refresh (a second tab of the same guest) takes control with a new epoch: the old tab is
    /// told, keeps watching, and its stale or spoofed items are refused with typed codes. The new tab learns the sequence floor from Resync (invoked
    /// while its own input stream is open), resumes above it and is acknowledged; below the floor it is refused with the floor in the reply. A spectator
    /// may resync; a stranger and the lobby are refused; spectator gameplay is refused at the transport and again at the application layer.
    /// </summary>
    [Fact]
    public async Task A_refresh_takes_control_with_the_sequence_floor_and_spoofs_and_spectators_are_refused_in_both_layers()
    {
        await using var s = await StartAsync(2, spectators: 1);
        var a = s.Players[0];
        var b = s.Players[1];
        s.StartPlaying(a, b);
        await Eventually(() => a.Acks.Count > 10 && b.Acks.Count > 10, "both controllers acknowledged");
        var floorBefore = a.Acks.Last().LastAppliedSeq;

        // The refresh: a second tab of A's guest.
        var tab2 = await GhostLapHubTests.OpenAsync(s.Stack, a.Login);
        var a2 = new GameClient(tab2, new NetworkConditions(), a.Login, () => s.Engine.State.Tick) { MatchId = a.MatchId, Slot = a.Slot };
        await using var _ = a2;
        var joined = await tab2.JoinAsync(s.RoomCode);
        Assert.True(joined.Ok, joined.Code);
        a2.Joined = joined.Value!;
        Assert.Equal(a.Joined.ControllerEpoch + 1, a2.Joined.ControllerEpoch);
        await Eventually(() => Volatile.Read(ref a.Tab.Superseded) == 1, "the old tab is told it was superseded");
        await Eventually(() => s.Engine.ControllerOf(a.Slot).Epoch == a2.Joined.ControllerEpoch, "the match saw the takeover");

        // The new tab asks for the truth while its stream is open: floor, epoch and role.
        a2.OpenStream();
        var reply = await ResyncAsync(a2);
        Assert.True(reply.Ok, reply.Code);
        var frame = reply.Value!;
        Assert.True(frame.IsResync);
        Assert.Equal((RoomRoles.Competitor, a.Slot, true), (frame.You.Role, frame.You.Slot, frame.You.CanControl));
        Assert.Equal(a2.Joined.ControllerEpoch, frame.You.ControllerEpoch);
        Assert.True(frame.You.AppliedSeq >= floorBefore, $"floor {frame.You.AppliedSeq} below the last acknowledgement {floorBefore}");
        Assert.Equal(frame.Snapshot.Tick, frame.Events.ThroughTick);
        Assert.True(a2.Filter.Offer(frame).Applied);

        // Below the floor it is refused, with the floor to resume from; above it, it is applied and acknowledged.
        a2.Send(1, 0, false, seq: 1);
        await Eventually(() => a2.RejectedSeqs.Contains(1), "stale sequence refused");
        var stale = a2.Tab.Rejections.First(r => r.Seq == 1);
        Assert.Equal(GhostLapFailure.StaleSequence, stale.Code);
        Assert.True(stale.SequenceFloor >= frame.You.AppliedSeq);
        var resumed = stale.SequenceFloor!.Value + 1;
        Volatile.Write(ref a2.Seq, resumed);
        a2.Send(0, 1, false, seq: resumed);
        await Eventually(() => a2.Acks.Any(x => x.LastAppliedSeq == resumed && x.ControllerEpoch == a2.Joined.ControllerEpoch), "the new controller is acknowledged");

        // The old tab: its own epoch and a spoofed new epoch are both refused and the refusal is delivered to it.
        var oldEpoch = a.Joined.ControllerEpoch;
        var tick = s.Engine.State.Tick;
        a.Inputs.Writer.TryWrite(new GameplayInput(a.MatchId, oldEpoch, tick + 1, 900_001, 1, 0, false));
        a.Inputs.Writer.TryWrite(new GameplayInput(a.MatchId, a2.Joined.ControllerEpoch, tick + 1, 900_002, 1, 0, true));
        await Eventually(() => a.Tab.Rejections.Count(r => r.Seq is 900_001 or 900_002) == 2, "old tab refused twice");
        Assert.All(a.Tab.Rejections.Where(r => r.Seq is 900_001 or 900_002), r => Assert.Equal(GhostLapFailure.Superseded, r.Code));
        await Eventually(() => a.Frames.Last().You is { CanControl: false } && a.Frames.Last().Snapshot.Tick > tick, "the old tab keeps receiving state as a viewer");
        Assert.Contains(s.Engine.State.World.Players, p => p.Slot == a.Slot && p.LastSequence < 900_001);

        // Spectator: read-only resync is fine, gameplay is refused at the transport and, called directly, at the application layer.
        var watcher = s.Spectators[0];
        var watcherFrame = (await ResyncAsync(watcher)).Value!;
        Assert.Equal(new RecipientView(RoomRoles.Spectator, -1, 0, false, -1), watcherFrame.You);
        watcher.MatchId = a.MatchId;
        watcher.OpenStream();
        watcher.Send(1, 0, false, seq: 1);
        await Eventually(() => watcher.Tab.Rejections.Any(r => r.Code == GhostLapFailure.SpectatorForbidden), "spectator refused at the transport");
        var before = s.Engine.State.World.Players.Select(p => (p.XFixed, p.YFixed, p.LastSequence)).ToArray();
        var memberId = watcher.Joined.MemberId;
        Assert.Equal(GhostLapFailure.Forbidden, s.Registry.Submit(new AdmittedInput(
            Guid.Parse(s.View.RoomId), memberId, "spectator", 0, new GameplayInput(a.MatchId, 0, tick + 1, 1, 1, 0, false), "")));
        Assert.Equal(before.Length, s.Engine.State.World.Players.Length);

        // A stranger (not in the room) cannot read a frame.
        var stranger = await GhostLapHubTests.GuestAsync(s.Stack);
        await using var outsider = new GameClient(await GhostLapHubTests.OpenAsync(s.Stack, stranger), new NetworkConditions(), stranger, () => 0);
        Assert.Equal(GhostLapFailure.RoomUnavailable, (await ResyncAsync(outsider)).Code);
    }

    /// <summary>**Validates: Requirements 10.08** Before a match exists there is nothing to resync: a typed refusal, not an empty frame.</summary>
    [Fact]
    public async Task Resync_in_a_lobby_is_a_typed_refusal()
    {
        await using var stack = _kit.NewStack(Settings());
        var guest = await GhostLapHubTests.GuestAsync(stack);
        await using var client = new GameClient(await GhostLapHubTests.OpenAsync(stack, guest), new NetworkConditions(), guest, () => 0);
        Assert.Equal(GhostLapFailure.RoomUnavailable, (await ResyncAsync(client)).Code);
        Assert.True((await client.Tab.CreateAsync()).Ok);
        Assert.Equal(GhostLapFailure.NoRunningMatch, (await ResyncAsync(client)).Code);
    }

    // ------------------------------------------------------------------ disconnect, grace, forfeit

    /// <summary>
    /// **Validates: Requirements 10.06, 10.07, 10.08** Real disconnects. B closes its only tab for good; C closes its tab and returns with a new one
    /// before the grace ends. A watches both through snapshots. B shows as in grace with the forfeit tick D + 450 and is forfeited exactly there with
    /// frozen score and one forfeit event; C keeps its slot, learns its sequence floor (the server neutral consumed one), resumes and is acknowledged.
    /// </summary>
    [Fact]
    public async Task A_vanished_competitor_is_forfeited_at_D_plus_450_and_one_that_returns_in_time_keeps_its_slot()
    {
        await using var s = await StartAsync(3, spectators: 0);
        var (a, b, c) = (s.Players[0], s.Players[1], s.Players[2]);
        s.StartPlaying(a, b, c);
        await Eventually(() => s.Players.All(p => p.Acks.Count > 5), "all acknowledged");
        await Task.Delay(1500);
        var c2Seq = c.Acks.Last().LastAppliedSeq;

        await b.DisposeAsync();
        await c.DisposeAsync();
        await Eventually(() => a.Last!.Players[b.Slot].Status == SlotStatuses.Grace && a.Last.Players[c.Slot].Status == SlotStatuses.Grace, "A sees both in grace");
        var graceB = a.Last!.Players[b.Slot];
        var graceC = a.Last.Players[c.Slot];
        Assert.False(graceB.Connected);
        var dB = graceB.GraceEndsTick - 450;
        Assert.InRange(dB, 1, s.Engine.State.Tick);
        output.WriteLine($"GRACE B: first observed disconnect tick D={dB}, forfeit due at {graceB.GraceEndsTick}; C due at {graceC.GraceEndsTick}");

        // C returns about 10 s (300 ticks) into its 450-tick grace.
        await Eventually(() => s.Engine.State.Tick >= dB + 300, "300 ticks of grace elapsed", 20_000);
        await Eventually(() => a.Last!.Players[c.Slot].Status == SlotStatuses.Grace, "still in grace");
        var guestC = c.Login;
        var tabC = await GhostLapHubTests.OpenAsync(s.Stack, guestC);
        var c2 = new GameClient(tabC, new NetworkConditions(), guestC, () => s.Engine.State.Tick) { MatchId = c.MatchId, Slot = c.Slot };
        await using var __ = c2;
        var joined = await tabC.JoinAsync(s.RoomCode);
        Assert.True(joined.Ok, joined.Code);
        c2.Joined = joined.Value!;
        c2.OpenStream();
        await Eventually(() => a.Last!.Players[c.Slot].Status == SlotStatuses.Active, "the loop sees the controller again", 5_000);
        var frame = (await ResyncAsync(c2)).Value!;
        Assert.True(frame.You.CanControl);
        Assert.Equal(SlotStatuses.Active, frame.Snapshot.Players[c.Slot].Status);
        Assert.True(frame.You.AppliedSeq >= c2Seq + 1, $"the server neutral consumed a sequence: floor {frame.You.AppliedSeq} vs last ack {c2Seq}");
        Volatile.Write(ref c2.Seq, frame.You.AppliedSeq);
        s.StartPlaying(c2);
        await Eventually(() => c2.Acks.Any(x => x.ControllerEpoch == c2.Joined.ControllerEpoch), "C acknowledged after the return");

        // Run past D + 450: B is forfeited exactly at D + 450, C is not.
        await Eventually(() => s.Engine.State.Tick >= dB + 460, "past the forfeit tick", 30_000);
        await Eventually(() => a.Last!.Tick >= dB + 455, "A has a snapshot after the forfeit");
        Assert.Equal(dB + 450, s.Engine.State.ForfeitTicks[b.Slot]);
        Assert.Equal(0, s.Engine.State.ForfeitTicks[c.Slot]);
        var after = a.Last!;
        Assert.Equal((SlotStatuses.Forfeited, dB + 450), (after.Players[b.Slot].Status, after.Players[b.Slot].ForfeitTick));
        Assert.Equal(SlotStatuses.Active, after.Players[c.Slot].Status);
        Assert.Single(a.AppliedEvents, e => e.Kind == MatchEventKinds.Forfeited && e.Slot == b.Slot && e.Tick == dB + 450);
        Assert.DoesNotContain(a.AppliedEvents, e => e.Kind == MatchEventKinds.Forfeited && e.Slot == c.Slot);
        // Frozen: B's state does not change after the forfeit.
        var frozen = after.Players[b.Slot];
        await Eventually(() => a.Last!.Tick >= after.Tick + 60, "60 more ticks");
        var later = a.Last!.Players[b.Slot];
        Assert.Equal((frozen.XFixed, frozen.YFixed, frozen.TotalScore, frozen.Charge), (later.XFixed, later.YFixed, later.TotalScore, later.Charge));
        Assert.Equal(0, s.Engine.State.World.Players[b.Slot].OverlitDeadlineTick);

        // B comes back after the forfeit: it may join the room again, but only to watch. Its items are refused with the typed reason.
        var tabB = await GhostLapHubTests.OpenAsync(s.Stack, b.Login);
        var b2 = new GameClient(tabB, new NetworkConditions(), b.Login, () => s.Engine.State.Tick) { MatchId = b.MatchId, Slot = b.Slot };
        await using var ___ = b2;
        var rejoined = await tabB.JoinAsync(s.RoomCode);
        Assert.True(rejoined.Ok, rejoined.Code);
        b2.Joined = rejoined.Value!;
        b2.OpenStream();
        var watching = (await ResyncAsync(b2)).Value!;
        Assert.Equal((RoomRoles.Competitor, false, SlotStatuses.Forfeited), (watching.You.Role, watching.You.CanControl, watching.Snapshot.Players[b.Slot].Status));
        b2.Send(1, 0, false, seq: watching.You.AppliedSeq + 1);
        await Eventually(() => b2.Tab.Rejections.Any(r => r.Code == GhostLapFailure.Forfeited), "a forfeited slot's input is refused with the typed reason");
        Assert.Equal(frozen.XFixed, s.Engine.State.World.Players[b.Slot].XFixed);
    }

    /// <summary>
    /// **Validates: Requirements 10.06, 10.07** Half-open and suspended connections, with a 1 s server ping and a 3 s client timeout. A stall shorter
    /// than the timeout is invisible to the server (no disconnect, the held input simply expires). A longer one is detected by the server alone
    /// (nothing reaches it), which starts the grace; the suspended tab then returns as a new tab inside the grace and keeps the slot.
    /// </summary>
    [Fact]
    public async Task A_short_stall_is_invisible_and_a_half_open_connection_is_detected_by_the_server_then_recovered_inside_the_grace()
    {
        var settings = Settings(("HubKeepAliveSeconds", "1"), ("HubClientTimeoutSeconds", "3"));
        var nets = new[] { new NetworkConditions(), new NetworkConditions() };
        await using var s = await StartAsync(2, settings, i => nets[i], configure: c => c.KeepAliveInterval = TimeSpan.FromSeconds(1));
        var (a, b) = (s.Players[0], s.Players[1]);
        s.StartPlaying(a, b);
        await Eventually(() => b.Acks.Count > 10, "B acknowledged");

        // 1. A 1.2 s stall (a tab that freezes briefly): nothing disconnects, B stays Active in A's view.
        var statuses = new ConcurrentQueue<string>();
        using var watch = new CancellationTokenSource();
        var watcher = Task.Run(async () =>
        {
            while (!watch.IsCancellationRequested)
            {
                statuses.Enqueue(a.Last!.Players[b.Slot].Status);
                await Task.Delay(30);
            }
        });
        nets[1].Blackhole = true;
        await Task.Delay(1200);
        nets[1].Blackhole = false;
        await Task.Delay(1500);
        Assert.DoesNotContain(SlotStatuses.Grace, statuses);
        Assert.NotNull(s.Engine.ControllerOf(b.Slot).ConnectionId);
        var ackedAfter = b.Acks.Count;
        await Eventually(() => b.Acks.Count > ackedAfter + 3, "B is acknowledged again after the stall");

        // 2. A half-open connection: the server hears nothing for longer than the client timeout.
        var detectStart = s.Engine.State.Tick;
        var wall = Stopwatch.StartNew();
        nets[1].Blackhole = true;
        await Eventually(() => a.Last!.Players[b.Slot].Status == SlotStatuses.Grace, "server detects the silent connection", 15_000);
        var detectedAfterMs = wall.Elapsed.TotalMilliseconds;
        await watch.CancelAsync();
        await watcher;
        var d = a.Last!.Players[b.Slot].GraceEndsTick - 450;
        output.WriteLine($"HALF-OPEN: client silent from tick {detectStart}; server observed the disconnect at tick D={d} after {detectedAfterMs:F0} ms of wall time (timeout 3 s, ping 1 s)");
        Assert.InRange(detectedAfterMs, 2_000, 6_500);
        Assert.Null(s.Engine.ControllerOf(b.Slot).ConnectionId);

        // 3. The tab resumes as a new connection inside the grace and keeps the slot.
        var tab = await GhostLapHubTests.OpenAsync(s.Stack, b.Login, configure: c => c.KeepAliveInterval = TimeSpan.FromSeconds(1));
        var b2 = new GameClient(tab, new NetworkConditions(), b.Login, () => s.Engine.State.Tick) { MatchId = b.MatchId, Slot = b.Slot };
        await using var _ = b2;
        var joined = await tab.JoinAsync(s.RoomCode);
        Assert.True(joined.Ok, joined.Code);
        b2.Joined = joined.Value!;
        b2.OpenStream();
        await Eventually(() => a.Last!.Players[b.Slot].Status == SlotStatuses.Active, "the loop sees the controller again", 5_000);
        var frame = (await ResyncAsync(b2)).Value!;
        Assert.Equal(SlotStatuses.Active, frame.Snapshot.Players[b.Slot].Status);
        Assert.True(frame.You.CanControl);
        Volatile.Write(ref b2.Seq, frame.You.AppliedSeq);
        s.StartPlaying(b2);
        await Eventually(() => b2.Acks.Any(), "the resumed tab is acknowledged");
        Assert.False(s.Engine.State.IsForfeited(b.Slot));
        nets[1].Blackhole = false; // the old, half-open socket finally hears the server close it
    }

    // ------------------------------------------------------------------ slow and saturated clients

    /// <summary>
    /// **Validates: Requirements 10.10** A spectator that stops reading entirely (its socket never drains) while two players keep playing: the
    /// authoritative tick keeps its pace, the players' acknowledgement latency stays normal, and the recipient is bounded: either it is cut by the
    /// send timeout or, if the in-memory transport applies no backpressure, nothing for it ever queues on the server.
    /// </summary>
    [Fact]
    public async Task A_spectator_that_stops_reading_does_not_stall_the_tick_or_the_players()
    {
        var settings = Settings(("SendTimeoutMilliseconds", "1000"));
        var nets = new[] { new NetworkConditions(), new NetworkConditions(), new NetworkConditions() };
        await using var s = await StartAsync(2, settings, i => i < 2 ? nets[i] : nets[2], spectators: 1);
        s.StartPlaying([.. s.Players]);
        await Task.Delay(3000);
        foreach (var p in s.Players)
        {
            p.ResetMetrics();
        }
        var watcher = s.Spectators[0];
        var stalled = s.Runtime.Fanout.Recipients.Single(r => r.Viewer.Spectator); // a client cannot know its server-side connection id
        var connectionId = stalled.ConnectionId;
        var framesBefore = watcher.Frames.Count;
        var tickFrom = s.Engine.State.Tick;
        var clock = Stopwatch.StartNew();
        watcher.Net.StopReading = true;
        await Task.Delay(12_000);
        var ticks = s.Engine.State.Tick - tickFrom;
        var seconds = clock.Elapsed.TotalSeconds;
        var recipient = s.Runtime.Fanout.Find(connectionId);
        var sentToSpectator = stalled.Stats.SnapshotsSent;
        output.WriteLine($"STALLED-SPECTATOR ticks={ticks} in {seconds:F2}s ({ticks / seconds:F2} Hz); recipient {(recipient is null ? "cut" : "kept")}; " +
                         $"stalled={stalled.Stats.Stalled}; skipped={stalled.Stats.SnapshotsSkipped}; sent={stalled.Stats.SnapshotsSent}; frames the spectator's client got={watcher.Frames.Count - framesBefore}");
        Assert.InRange(ticks / seconds, 29.0, 31.0);
        await s.StopPlayingAsync();
        foreach (var p in s.Players)
        {
            var sum = p.Summary();
            output.WriteLine(GameClient.Format($"player {p.Slot} while a spectator was stalled", sum));
            Assert.True(sum.AckP95 < 150, $"ack p95 {sum.AckP95:F0} ms");
            Assert.True(sum.AgeP95 < 150, $"snapshot age p95 {sum.AgeP95:F0} ms");
        }
        Assert.True(s.Engine.TotalRejected < s.Players.Sum(p => p.SentCount) / 20, $"rejected {s.Engine.TotalRejected}");
        Assert.True(s.Engine.QueuedInputs <= 16);
        Assert.NotNull(s.Runtime.Driver!.Scheduler);
        Assert.True(s.Runtime.Driver.Scheduler!.Telemetry.MaxBacklogTicks <= 3);
        _ = sentToSpectator;
    }
}
