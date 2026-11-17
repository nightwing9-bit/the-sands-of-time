using System.Collections.Concurrent;
using GhostLap.Contracts.Multiplayer;
using GhostLap.Multiplayer;
using GhostLap.Rules;
using GhostLap.Simulation;
using InputRejection = GhostLap.Contracts.Multiplayer.InputRejection;
using GhostLap.Tests.Simulation;
using Xunit;
using Xunit.Abstractions;
using static GhostLap.Tests.Multiplayer.MatchTestKit;

namespace GhostLap.Tests.Multiplayer;

/// <summary>A real <see cref="IMatchTransport"/> whose clients a test can slow down, block or let read normally. Everything else is production code.</summary>
internal sealed class TestTransport : IMatchTransport
{
    public sealed class Client
    {
        public ConcurrentQueue<SnapshotFrame> Frames { get; } = new();
        public ConcurrentQueue<InputAck> Acks { get; } = new();
        public ConcurrentQueue<InputRejection> Rejections { get; } = new();
        public ConcurrentQueue<MatchControlNotice> Controls { get; } = new();
        public volatile TaskCompletionSource? Gate;
        public volatile int DelayMs;
        public int Aborts;

        public async ValueTask PassAsync(CancellationToken ct)
        {
            if (Gate is { } gate)
            {
                await gate.Task.WaitAsync(ct);
            }
            if (DelayMs > 0)
            {
                await Task.Delay(DelayMs, ct);
            }
        }

        public void Block() => Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => Gate?.TrySetResult();
    }

    private readonly ConcurrentDictionary<string, Client> _clients = new();

    public Client Of(string id) => _clients.GetOrAdd(id, _ => new Client());

    public async ValueTask SendSnapshotAsync(string connectionId, SnapshotFrame frame, CancellationToken ct)
    {
        var c = Of(connectionId);
        await c.PassAsync(ct);
        c.Frames.Enqueue(frame);
    }

    public async ValueTask SendAckAsync(string connectionId, InputAck ack, CancellationToken ct)
    {
        var c = Of(connectionId);
        await c.PassAsync(ct);
        c.Acks.Enqueue(ack);
    }

    public async ValueTask SendRejectionAsync(string connectionId, InputRejection rejection, CancellationToken ct)
    {
        var c = Of(connectionId);
        await c.PassAsync(ct);
        c.Rejections.Enqueue(rejection);
    }

    public async ValueTask SendControlAsync(string connectionId, MatchControlNotice notice, CancellationToken ct)
    {
        var c = Of(connectionId);
        await c.PassAsync(ct);
        c.Controls.Enqueue(notice);
    }

    public void Abort(string connectionId) => Interlocked.Increment(ref Of(connectionId).Aborts);
}

/// <summary>
/// Task 10.3 (R10.08, R10.10): the feed (snapshots, event ids, bounded tail), the client causality filter and the per-recipient fan-out, with the
/// real engine, reducer, feed, fan-out and filter. Only the socket is a <see cref="TestTransport"/>.
/// </summary>
public sealed class SnapshotFanoutTests(ITestOutputHelper output)
{
    private static readonly MultiplayerOptions Fast = new() { InputsPerSecond = 1000, InputBurst = 1000, EventRetentionTicks = 2700 };

    private sealed class Rig
    {
        public required MatchRuntime Runtime { get; init; }
        public required ManualTime Clock { get; init; }
        public required SwitchableAuthority Authority { get; init; }
        public required TestTransport Transport { get; init; }
        public required CandleSeekingDriver Driver { get; init; }
        public required GameContext V1 { get; init; }
        public List<TickOutcome> Outcomes { get; } = [];
        public MatchEngine Engine => Runtime.Engine;

        public MatchFanout Fanout => Runtime.Fanout;

        /// <summary>One real tick: every connected slot's driver input is enqueued through the real ingress, then the loop steps and publishes.</summary>
        public TickOutcome Tick(bool drive = true)
        {
            Clock.Advance(TimeSpan.FromMilliseconds(34));
            if (drive)
            {
                foreach (var g in Driver.Inputs(Engine.State.World, V1))
                {
                    if (Engine.ControllerOf(g.Slot).ConnectionId is not null)
                    {
                        Assert.Null(Send(Engine, g.Slot, g.Sequence, g.MoveX, g.MoveY, g.RequestBurst));
                    }
                }
            }
            var outcome = Engine.Step();
            Runtime.PublishTick(outcome);
            Outcomes.Add(outcome);
            return outcome;
        }
    }

    private static Rig NewRig(int players, MultiplayerOptions? options = null, byte secret = 7, Action<TestTransport>? beforeAttach = null)
    {
        options ??= Fast;
        var clock = new ManualTime();
        var authority = new SwitchableAuthority(Generation);
        var transport = new TestTransport();
        beforeAttach?.Invoke(transport);
        var runtime = MatchRuntime.Create(Seal(players, secret, options), options, clock, authority, transport, TimeProvider.System);
        for (var slot = 0; slot < players; slot++)
        {
            runtime.Engine.SetController(new SlotControl(slot, 1, $"c{slot}"));
            runtime.Fanout.Attach(new MatchViewer($"c{slot}", runtime.Config.Slots[slot].MemberId, false, slot, 1, true));
        }
        return new Rig
        {
            Runtime = runtime,
            Clock = clock,
            Authority = authority,
            Transport = transport,
            Driver = new CandleSeekingDriver(players),
            V1 = GameContext.Create(runtime.Engine.Context.Seed, players),
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
            await Task.Delay(5);
        }
    }

    // ------------------------------------------------------------------ the feed

    /// <summary>
    /// **Validates: Requirements 10.08** Snapshots are published every second tick with rising sequences, carry exactly the authoritative state of
    /// their tick, and their event identities are unique, ordered and complete (the tail equals what every tick resolved).
    /// </summary>
    [Fact]
    public void Snapshots_are_every_second_tick_and_events_have_unique_ordered_complete_identities()
    {
        var rig = NewRig(3, secret: 9);
        var stateAt = new Dictionary<long, (int X, int Y, int Score)[]>();
        var seen = new List<MatchSnapshot>();
        for (var i = 0; i < 400; i++)
        {
            var outcome = rig.Tick();
            stateAt[outcome.Tick] = [.. rig.Engine.State.World.Players.Select(p => (p.XFixed, p.YFixed, p.TotalScore))];
            if (rig.Runtime.Feed.Latest!.Snapshot is { } snap && (seen.Count == 0 || seen[^1].Sequence != snap.Sequence))
            {
                seen.Add(snap);
            }
        }
        Assert.Equal(Enumerable.Range(0, 201).Select(i => (long)i), seen.Select(s => s.Sequence));
        Assert.Equal(Enumerable.Range(0, 201).Select(i => i * 2L), seen.Select(s => s.Tick));
        foreach (var s in seen.Skip(1))
        {
            Assert.Equal(stateAt[s.Tick], s.Players.Select(p => (p.XFixed, p.YFixed, p.TotalScore)));
            Assert.Equal(rig.Runtime.Config.MatchId.ToString("N"), s.MatchId);
            Assert.Equal(Generation.ToString("N"), s.GenerationId);
        }
        var latest = rig.Runtime.Feed.Latest!;
        var tail = rig.Runtime.Feed.EventsAfter(latest, 0);
        var oracle = rig.Outcomes.Where(o => o.Tick <= latest.Snapshot.Tick).SelectMany(o => o.Events).ToArray();
        Assert.False(tail.Truncated);
        Assert.Equal(oracle, tail.Items);
        Assert.NotEmpty(oracle);
        Assert.Equal(oracle.Select(e => e.Key).Order(), oracle.Select(e => e.Key));
        Assert.Equal(oracle.Length, oracle.Select(e => e.Key).Distinct().Count());
        Assert.Equal(latest.Snapshot.Players.Sum(p => p.Pickups), oracle.Count(e => e.Kind == MatchEventKinds.CandleCollected));
    }

    /// <summary>**Validates: Requirements 10.08** The retained tail is exactly <c>EventRetentionTicks</c> ticks; older cursors are told they were cut.</summary>
    [Theory]
    [InlineData(169, true)]   // needs tick 170, which is gone
    [InlineData(170, false)]  // needs 171.., all retained
    [InlineData(185, false)]
    public void The_event_tail_is_bounded_and_reports_when_a_cursor_fell_off_it(long cursor, bool truncated)
    {
        var rig = NewRig(2, new MultiplayerOptions { InputsPerSecond = 1000, InputBurst = 1000, EventRetentionTicks = 30 });
        for (var i = 0; i < 200; i++)
        {
            rig.Tick();
        }
        var latest = rig.Runtime.Feed.Latest!;
        Assert.Equal(200, latest.Snapshot.Tick); // retained ticks are 171..200
        var slice = rig.Runtime.Feed.EventsAfter(latest, cursor);
        Assert.Equal(truncated, slice.Truncated);
        Assert.Equal(rig.Outcomes.Where(o => o.Tick > Math.Max(cursor, 170)).SelectMany(o => o.Events), slice.Items);
        Assert.Equal(200, slice.ThroughTick);
        Assert.Equal(truncated ? 171 : cursor + 1, slice.FromTick);
    }

    /// <summary>**Validates: Requirements 10.06, 10.08** A disconnect and a forfeit publish at once (not at the next interval) and carry the slot status.</summary>
    [Fact]
    public void A_disconnect_and_a_forfeit_publish_immediately_with_the_slot_status()
    {
        var rig = NewRig(2, secret: 4);
        for (var i = 0; i < 4; i++)
        {
            rig.Tick();
        }
        rig.Engine.SetController(new SlotControl(1, 1, null));
        rig.Runtime.Fanout.Detach("c1");
        var odd = rig.Tick(); // tick 5: odd, not an interval tick, but the disconnect is published
        Assert.Equal(5, odd.Tick);
        var s5 = rig.Runtime.Feed.Latest!.Snapshot;
        Assert.Equal(5, s5.Tick);
        Assert.Equal((SlotStatuses.Grace, false, 455L), (s5.Players[1].Status, s5.Players[1].Connected, s5.Players[1].GraceEndsTick));
        Assert.Equal(SlotStatuses.Active, s5.Players[0].Status);
        while (rig.Engine.State.Tick < 454)
        {
            rig.Tick();
        }
        Assert.Equal(SlotStatuses.Grace, rig.Runtime.Feed.Latest!.Snapshot.Players[1].Status);
        var forfeit = rig.Tick(); // tick 455 = D + 450 where D = 5: even/odd irrelevant, a forfeit always publishes
        Assert.Equal([1], forfeit.Forfeited);
        var last = rig.Runtime.Feed.Latest!.Snapshot;
        Assert.Equal(455, last.Tick);
        Assert.Equal((SlotStatuses.Forfeited, 455L, 0L), (last.Players[1].Status, last.Players[1].ForfeitTick, last.Players[1].GraceEndsTick));
        Assert.Contains(rig.Runtime.Feed.EventsAfter(rig.Runtime.Feed.Latest!, 454).Items, e => e is { Kind: MatchEventKinds.Forfeited, Slot: 1, Index: 0 });
    }

    // ------------------------------------------------------------------ the client filter

    private static SnapshotFrame Frame(string match, string generation, long sequence, long lifecycle, bool resync = false, params MatchEvent[] events) => new(
        new MatchSnapshot(match, generation, "ghost-mp-v1", "arena-v1", sequence * 2, sequence, false, [], []),
        lifecycle,
        lifecycle == 1 ? MatchStatuses.Running : MatchStatuses.PendingFinalization,
        new RecipientView(RoomRoles.Spectator, -1, 0, false, -1),
        new EventBatch(events.Length == 0 ? 0 : events.Min(e => e.Tick), sequence * 2, false, events),
        resync);

    private static MatchEvent Ev(long tick, int index) => new(tick, index, MatchEventKinds.CandleCollected, 0, 0, 25, "c");

    /// <summary>
    /// **Validates: Requirements 10.08, 10.05** Order is (lifecycle, sequence) inside one (match, generation); generation ids are opaque and never
    /// ordered; another match or generation is adopted only by an explicit resync; duplicates and late frames change nothing.
    /// </summary>
    [Fact]
    public void The_client_filter_orders_by_lifecycle_and_sequence_and_treats_generation_ids_as_opaque()
    {
        var f = new SnapshotFilter();
        Assert.Equal(SnapshotVerdict.Baseline, f.Offer(Frame("m1", "zzz", 4, 1)).Verdict);          // first frame is a baseline
        Assert.Equal(SnapshotVerdict.Accepted, f.Offer(Frame("m1", "zzz", 5, 1)).Verdict);
        Assert.Equal(SnapshotVerdict.Stale, f.Offer(Frame("m1", "zzz", 5, 1)).Verdict);             // duplicate
        Assert.Equal(SnapshotVerdict.Stale, f.Offer(Frame("m1", "zzz", 3, 1)).Verdict);             // delayed
        Assert.Equal(SnapshotVerdict.Accepted, f.Offer(Frame("m1", "zzz", 5, 2)).Verdict);          // lifecycle rose: same state, new status
        Assert.Equal(SnapshotVerdict.Stale, f.Offer(Frame("m1", "zzz", 9, 1)).Verdict);             // a running frame never resurrects a finished match
        Assert.Equal(SnapshotVerdict.ForeignGeneration, f.Offer(Frame("m1", "aaa", 99, 9)).Verdict); // "aaa" < "zzz" and "zzz" < "aaa" mean nothing
        Assert.Equal(SnapshotVerdict.ForeignGeneration, f.Offer(Frame("m1", "zzzz", 99, 9)).Verdict);
        Assert.Equal(SnapshotVerdict.ForeignMatch, f.Offer(Frame("m2", "zzz", 99, 9)).Verdict);
        Assert.Equal((5L, 2L), (f.Sequence, f.LifecycleVersion));
        // A resync of the same state is a baseline (idempotent); a lower one is stale; another match is adopted (a rematch).
        Assert.Equal(SnapshotVerdict.Baseline, f.Offer(Frame("m1", "zzz", 5, 2, resync: true)).Verdict);
        Assert.Equal(SnapshotVerdict.Stale, f.Offer(Frame("m1", "zzz", 4, 2, resync: true)).Verdict);
        Assert.Equal(SnapshotVerdict.Baseline, f.Offer(Frame("m2", "aaa", 0, 1, resync: true)).Verdict);
        Assert.Equal(("m2", "aaa", 0L), (f.MatchId, f.GenerationId, f.Sequence));
    }

    /// <summary>**Validates: Requirements 10.08** Events apply once, by identity, across duplicates, coalescing and a resync; a hole is reported as a gap.</summary>
    [Fact]
    public void The_client_filter_applies_each_event_once_and_reports_holes()
    {
        var f = new SnapshotFilter();
        Assert.Equal([1L * 1024, 1L * 1024 + 1], f.Offer(Frame("m", "g", 1, 1, true, Ev(1, 0), Ev(1, 1))).NewEvents.Select(e => e.Key));
        var next = f.Offer(Frame("m", "g", 2, 1, false, Ev(1, 1), Ev(2, 0), Ev(3, 0))); // overlaps what was already applied
        Assert.Equal([2L * 1024, 3L * 1024], next.NewEvents.Select(e => e.Key));
        Assert.False(next.EventGap);
        Assert.Empty(f.Offer(Frame("m", "g", 2, 1, false, Ev(2, 0))).NewEvents);              // duplicate frame
        Assert.Empty(f.Offer(Frame("m", "g", 3, 1, true, Ev(1, 0), Ev(3, 0))).NewEvents);      // resync replays the tail: nothing new
        var hole = f.Offer(Frame("m", "g", 9, 1, false, Ev(9, 0)));                             // ticks 4..8 never arrived
        Assert.True(hole.EventGap);
        Assert.Equal([9L * 1024], hole.NewEvents.Select(e => e.Key));
    }

    // ------------------------------------------------------------------ per-recipient fan-out

    private static async Task<SnapshotFrame> LastFrameAsync(TestTransport.Client client, Func<SnapshotFrame, bool> until, string what, int ms = 15_000)
    {
        await Eventually(() => client.Frames.Any(until), what, ms);
        return client.Frames.Last(until);
    }

    /// <summary>
    /// **Validates: Requirements 10.10** A recipient that does not read cannot delay another recipient's acknowledgements or snapshots, nor the tick:
    /// the loop runs 600 ticks and a second client keeps receiving the latest state. When the blocked client reads again it gets the NEWEST state
    /// at once (stale snapshots were dropped), every event it missed (nothing retained was lost) and no older snapshot.
    /// </summary>
    [Fact]
    public async Task A_blocked_recipient_delays_nobody_and_recovers_with_the_newest_state_and_every_missed_event()
    {
        var rig = NewRig(2, beforeAttach: t => t.Of("c1").Block());
        var spectator = new MatchViewer("s0", "spectator-member", true, -1, 0, false);
        rig.Fanout.Attach(spectator);
        var started = DateTime.UtcNow;
        for (var i = 0; i < 600; i++)
        {
            rig.Tick();
        }
        var tickSeconds = (DateTime.UtcNow - started).TotalSeconds;
        Assert.True(tickSeconds < 5, $"600 ticks took {tickSeconds:F2} s with a blocked recipient");
        var live = await LastFrameAsync(rig.Transport.Of("c0"), f => f.Snapshot.Tick == 600, "c0 reaches tick 600");
        await LastFrameAsync(rig.Transport.Of("s0"), f => f.Snapshot.Tick == 600, "the spectator reaches tick 600");
        Assert.Empty(rig.Transport.Of("c1").Frames); // still blocked on its first send
        Assert.True(rig.Transport.Of("c0").Acks.Last().Tick >= 590); // acknowledgements flow for the healthy controller
        Assert.Equal(rig.Engine.State.World.Players[0].LastSequence, live.You.AppliedSeq);

        rig.Transport.Of("c1").Release();
        var recovered = await LastFrameAsync(rig.Transport.Of("c1"), f => f.Snapshot.Tick == 600, "c1 recovers to tick 600");
        var filter = new SnapshotFilter();
        var applied = new List<MatchEvent>();
        foreach (var frame in rig.Transport.Of("c1").Frames)
        {
            var r = filter.Offer(frame);
            applied.AddRange(r.NewEvents);
            Assert.False(r.EventGap);
        }
        Assert.Equal(rig.Outcomes.SelectMany(o => o.Events), applied);
        Assert.Equal(recovered.Snapshot.Sequence, filter.Sequence);
        var stats = rig.Fanout.Find("c1")!.Stats;
        // Every snapshot the blocked client never saw was dropped as stale, and it was counted exactly: the gaps between the sequences it did get.
        var sequences = rig.Transport.Of("c1").Frames.Select(f => f.Snapshot.Sequence).ToArray();
        Assert.Equal(sequences.Zip(sequences.Skip(1), (a, b) => b - a - 1).Sum(), stats.SnapshotsSkipped);
        Assert.Equal(300, sequences[^1]);
        Assert.True(sequences.Length <= 3, $"the blocked client got {sequences.Length} frames, not a backlog of 300");
        output.WriteLine($"BLOCKED-RECIPIENT ticks={tickSeconds:F3}s skipped={stats.SnapshotsSkipped} frames-after-release={rig.Transport.Of("c1").Frames.Count} events={applied.Count}");
    }

    /// <summary>
    /// **Validates: Requirements 10.10, 10.08** When the retained tail is shorter than the congestion, the recovered frame is a resync baseline and
    /// says the tail was truncated; the state is still exact and no stale frame is delivered.
    /// </summary>
    [Fact]
    public async Task Recovery_beyond_the_retained_tail_is_a_resync_baseline_with_exact_state()
    {
        var rig = NewRig(2, new MultiplayerOptions { InputsPerSecond = 1000, InputBurst = 1000, EventRetentionTicks = 30 });
        await Eventually(() => rig.Transport.Of("c1").Frames.Count == 1, "baseline delivered");
        rig.Transport.Of("c1").Block();
        for (var i = 0; i < 300; i++)
        {
            rig.Tick();
        }
        await LastFrameAsync(rig.Transport.Of("c0"), f => f.Snapshot.Tick == 300, "c0 current");
        rig.Transport.Of("c1").Release();
        var recovered = await LastFrameAsync(rig.Transport.Of("c1"), f => f.Snapshot.Tick == 300, "c1 recovered");
        Assert.True(recovered.IsResync);
        Assert.Equal(300, recovered.Events.ThroughTick);
        Assert.Equal(271, recovered.Events.FromTick); // retained ticks 271..300
        Assert.All(recovered.Events.Items, e => Assert.InRange(e.Tick, 271, 300));
        Assert.Equal(rig.Engine.State.World.Players[1].TotalScore, recovered.Snapshot.Players[1].TotalScore);
        var ticks = rig.Transport.Of("c1").Frames.Select(f => f.Snapshot.Tick).ToArray();
        Assert.Equal(ticks.Order(), ticks);
    }

    /// <summary>**Validates: Requirements 10.10** A recipient whose send exceeds the timeout is cut (aborted and forgotten) and nobody else is affected.</summary>
    [Fact]
    public async Task A_send_that_exceeds_the_timeout_cuts_only_that_recipient()
    {
        var rig = NewRig(2, new MultiplayerOptions { InputsPerSecond = 1000, InputBurst = 1000, SendTimeoutMilliseconds = 200 }, beforeAttach: t => t.Of("c1").Block());
        for (var i = 0; i < 60; i++)
        {
            rig.Tick();
        }
        await Eventually(() => rig.Transport.Of("c1").Aborts == 1, "the stalled connection is aborted");
        Assert.Null(rig.Fanout.Find("c1"));
        Assert.NotNull(rig.Fanout.Find("c0"));
        await LastFrameAsync(rig.Transport.Of("c0"), f => f.Snapshot.Tick == 60, "c0 unaffected");
        Assert.Equal(0, rig.Transport.Of("c0").Aborts);
    }

    /// <summary>
    /// **Validates: Requirements 10.03, 10.08** Acknowledgements are cumulative: an unsent one is replaced by a newer one, never by an older one, and the
    /// last applied sequence always arrives.
    /// </summary>
    [Fact]
    public async Task Acknowledgements_coalesce_to_the_newest_and_never_go_backwards()
    {
        var rig = NewRig(2);
        await Eventually(() => rig.Transport.Of("c0").Frames.Count == 1, "baseline");
        rig.Transport.Of("c0").Block();
        for (var i = 0; i < 100; i++)
        {
            rig.Tick();
        }
        rig.Fanout.OfferAck("c0", new InputAck("m", 1, 3, 3)); // an old acknowledgement arriving late must not replace a newer one
        rig.Transport.Of("c0").Release();
        await Eventually(() => rig.Transport.Of("c0").Acks.Any(a => a.Tick >= 96), "the newest acknowledgement arrives");
        var acks = rig.Transport.Of("c0").Acks.ToArray();
        Assert.Equal(acks.Select(a => a.Tick).Order(), acks.Select(a => a.Tick));
        Assert.Equal(acks.Select(a => a.LastAppliedSeq).Order(), acks.Select(a => a.LastAppliedSeq));
        Assert.True(acks.Length < 10, $"{acks.Length} acknowledgements for 100 applied ticks");
        Assert.True(rig.Fanout.Find("c0")!.Stats.AcksCoalesced > 70, // the driver skips an input on every fifth tick, so about 80 acknowledgements were offered
             $"coalesced {rig.Fanout.Find("c0")!.Stats.AcksCoalesced}, acks {string.Join(",", acks.Select(a => a.Tick))}");
        Assert.Equal(rig.Engine.State.World.Players[0].LastSequence, acks[^1].LastAppliedSeq);
    }

    /// <summary>
    /// **Validates: Requirements 10.03, 10.10** Rejections are reliable, ordered and bounded: a stalled client's queue holds exactly its capacity, the
    /// overflow is counted and reported once in a control notice, and nothing blocks the producer.
    /// </summary>
    [Fact]
    public async Task Rejections_are_reliable_ordered_and_bounded_with_one_overflow_notice()
    {
        var rig = NewRig(2, new MultiplayerOptions { InputsPerSecond = 1000, InputBurst = 1000, ControlQueueCapacity = 64 });
        await Eventually(() => rig.Transport.Of("c0").Frames.Count == 1, "baseline");
        rig.Transport.Of("c0").Block();
        rig.Fanout.OfferRejection("c0", new InputRejection(0, GhostLapFailure.RateLimited)); // taken by the sender, which then blocks on it
        await Task.Delay(150);
        for (var i = 1; i <= 200; i++)
        {
            rig.Fanout.OfferRejection("c0", new InputRejection(i, GhostLapFailure.StaleSequence, 7, 1));
        }
        rig.Transport.Of("c0").Release();
        await Eventually(() => rig.Transport.Of("c0").Controls.Any(), "overflow notice");
        await Eventually(() => rig.Transport.Of("c0").Rejections.Count >= 65, "queued rejections delivered");
        var seqs = rig.Transport.Of("c0").Rejections.Select(r => r.Seq).ToArray();
        Assert.Equal(seqs.Order(), seqs);
        Assert.Equal(65, seqs.Length); // the one the sender already held plus the 64 the queue kept
        var notice = Assert.Single(rig.Transport.Of("c0").Controls);
        Assert.Equal(MatchControlCodes.ControlOverflow, notice.Code);
        Assert.Equal("136", notice.Detail);
        Assert.All(rig.Transport.Of("c0").Rejections.Skip(1), r => Assert.Equal(7, r.SequenceFloor));
    }

    /// <summary>**Validates: Requirements 10.05, 10.08** After authority is lost nothing more is published to anyone (no stale-authority gameplay delivery).</summary>
    [Fact]
    public async Task Nothing_is_published_after_authority_is_lost()
    {
        var rig = NewRig(2);
        for (var i = 0; i < 40; i++)
        {
            rig.Tick();
        }
        await LastFrameAsync(rig.Transport.Of("c0"), f => f.Snapshot.Tick == 40, "live");
        rig.Authority.Lose();
        await Task.Delay(50);
        var frames = rig.Transport.Of("c0").Frames.Count + rig.Transport.Of("c1").Frames.Count;
        var acks = rig.Transport.Of("c0").Acks.Count;
        for (var i = 0; i < 100; i++)
        {
            rig.Tick();
        }
        await Task.Delay(200);
        Assert.Equal(frames, rig.Transport.Of("c0").Frames.Count + rig.Transport.Of("c1").Frames.Count);
        Assert.Equal(acks, rig.Transport.Of("c0").Acks.Count);
    }

    /// <summary>
    /// **Validates: Requirements 10.17, 10.06, 10.03** The recipient's own view: a spectator and a superseded tab can only watch, a forfeited slot's
    /// connection can only watch and is told so once, and a controller sees the sequence floor it must resume above (the neutral included).
    /// </summary>
    [Fact]
    public async Task Each_recipient_sees_its_own_role_floor_and_forfeit_notice()
    {
        var rig = NewRig(2, secret: 3);
        rig.Fanout.Attach(new MatchViewer("watch", "w", true, -1, 0, false));
        rig.Fanout.Attach(new MatchViewer("c0-old", rig.Runtime.Config.Slots[0].MemberId, false, 0, 1, false)); // a superseded tab of slot 0
        for (var i = 0; i < 4; i++)
        {
            rig.Tick();
        }
        var spectator = await LastFrameAsync(rig.Transport.Of("watch"), f => f.Snapshot.Tick == 4, "spectator frame");
        Assert.Equal(new RecipientView(RoomRoles.Spectator, -1, 0, false, -1), spectator.You);
        var old = await LastFrameAsync(rig.Transport.Of("c0-old"), f => f.Snapshot.Tick == 4, "old tab frame");
        Assert.Equal((RoomRoles.Competitor, 0, false), (old.You.Role, old.You.Slot, old.You.CanControl));
        var controller = await LastFrameAsync(rig.Transport.Of("c0"), f => f.Snapshot.Tick == 4, "controller frame");
        Assert.True(controller.You.CanControl);
        Assert.Equal(rig.Engine.State.World.Players[0].LastSequence, controller.You.AppliedSeq);

        // Slot 1 disconnects: the server neutral consumes a sequence number, which its next controller learns from the floor.
        rig.Engine.SetController(new SlotControl(1, 1, null));
        var before = rig.Engine.State.World.Players[1].LastSequence;
        while (rig.Engine.State.Tick < 460)
        {
            rig.Tick();
        }
        Assert.True(rig.Engine.State.IsForfeited(1));
        var seen = await LastFrameAsync(rig.Transport.Of("c1"), f => f.Snapshot.Tick == 460, "forfeited slot's connection still watches");
        Assert.Equal((false, SlotStatuses.Forfeited), (seen.You.CanControl, seen.Snapshot.Players[1].Status));
        Assert.Equal(before + 1, seen.You.AppliedSeq); // exactly the neutral
        var notice = Assert.Single(rig.Transport.Of("c1").Controls);
        Assert.Equal((MatchControlCodes.Forfeited, "1", 455L), (notice.Code, notice.Detail, notice.Tick)); // D = 5, F = 455
    }

    /// <summary>
    /// **Validates: Requirements 10.08, 10.10** Resync returns state and event boundary of the same tick, becomes the recipient's baseline, and the
    /// pushed stream after it never repeats a cue: through a resync and heavy coalescing every event is applied exactly once, in order.
    /// </summary>
    [Fact]
    public async Task A_resync_is_consistent_and_the_stream_after_it_repeats_no_cue()
    {
        var rig = NewRig(3, secret: 11);
        rig.Fanout.Attach(new MatchViewer("late", "w", true, -1, 0, false)); // joins before tick 1 but is read slowly
        rig.Transport.Of("late").DelayMs = 3;
        var filter = new SnapshotFilter();
        var applied = new List<MatchEvent>();
        SnapshotFrame? resync = null;
        for (var i = 0; i < 900; i++)
        {
            rig.Tick();
            if (i == 500)
            {
                resync = rig.Fanout.Resync("late");
                Assert.NotNull(resync);
                Assert.True(resync.IsResync);
                Assert.Equal(resync.Snapshot.Tick, resync.Events.ThroughTick);
                Assert.Equal(rig.Runtime.Feed.Latest!.Snapshot.Sequence, resync.Snapshot.Sequence);
                // The hub answers the invocation with this frame; the client feeds it to the same filter as the pushed ones.
                var r = filter.Offer(resync);
                Assert.True(r.Applied);
                applied.AddRange(r.NewEvents);
            }
        }
        await LastFrameAsync(rig.Transport.Of("late"), f => f.Snapshot.Tick == 900, "late reader reaches 900");
        foreach (var frame in rig.Transport.Of("late").Frames)
        {
            var r = filter.Offer(frame);
            applied.AddRange(r.NewEvents);
            Assert.False(r.EventGap);
        }
        Assert.Equal(applied.Select(e => e.Key).Order(), applied.Select(e => e.Key));
        Assert.Equal(applied.Count, applied.Select(e => e.Key).Distinct().Count());
        Assert.Equal(rig.Outcomes.SelectMany(o => o.Events).Select(e => e.Key), applied.Select(e => e.Key));
    }

    /// <summary>
    /// **Validates: Requirements 10.08, 10.10, 10.09** A whole 2700-tick round for four recipients with different, nonzero latencies: every recipient's
    /// client filter ends on the final snapshot, applies every event exactly once, never sees a stale or foreign frame, and the queues stay bounded.
    /// </summary>
    [Fact]
    public async Task A_full_round_delivers_every_event_once_to_four_recipients_of_different_speed()
    {
        var rig = NewRig(4, secret: 21);
        rig.Transport.Of("c1").DelayMs = 1;
        rig.Transport.Of("c2").DelayMs = 4;
        rig.Transport.Of("c3").DelayMs = 12;
        rig.Fanout.Attach(new MatchViewer("watch", "w", true, -1, 0, false));
        rig.Transport.Of("watch").DelayMs = 25;
        while (!rig.Engine.State.IsFinal)
        {
            rig.Tick();
        }
        Assert.Equal(GameRulesV1.RoundTicks, rig.Engine.State.Tick);
        var oracle = rig.Outcomes.SelectMany(o => o.Events).Select(e => e.Key).ToArray();
        foreach (var id in new[] { "c0", "c1", "c2", "c3", "watch" })
        {
            var last = await LastFrameAsync(rig.Transport.Of(id), f => f.Snapshot.IsFinal, $"{id} receives the final snapshot", 30_000);
            var filter = new SnapshotFilter();
            var keys = new List<long>();
            foreach (var frame in rig.Transport.Of(id).Frames)
            {
                var r = filter.Offer(frame);
                Assert.True(r.Applied, $"{id}: {r.Verdict}");
                Assert.False(r.EventGap, id);
                keys.AddRange(r.NewEvents.Select(e => e.Key));
            }
            Assert.Equal(oracle, keys);
            Assert.Equal(last.Snapshot.Sequence, filter.Sequence);
            Assert.Equal(2700, last.Snapshot.Tick);
            var stats = rig.Fanout.Find(id)!.Stats;
            output.WriteLine($"{id}: frames={rig.Transport.Of(id).Frames.Count} skipped={stats.SnapshotsSkipped} maxSendUs={stats.MaxSendMicroseconds} events={keys.Count}");
        }
        Assert.Equal(0, rig.Fanout.Find("c0")!.Stats.Failures);
    }
}
