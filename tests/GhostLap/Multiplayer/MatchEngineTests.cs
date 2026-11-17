using GhostLap.Contracts.Multiplayer;
using GhostLap.Multiplayer;
using GhostLap.Rules;
using GhostLap.Simulation;
using GhostLap.Tests.Simulation;
using Xunit;
using static GhostLap.Tests.Multiplayer.MatchTestKit;
using static GhostLap.Tests.Simulation.GameTestKit;

namespace GhostLap.Tests.Multiplayer;

/// <summary>
/// Task 10.2: the per-room serial mailbox and authoritative tick of <see cref="MatchEngine"/>. Real engine, real reducer, real
/// Spec 06 admission policy; only the connection ids are strings chosen by the test.
/// </summary>
public sealed class MatchEngineTests
{
    private static readonly (int X, int Y)[] Directions = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    /// <summary>**Validates: Requirements 10.03, 10.04, 10.08** 2, 3 and 4 controllers drive ONE world; each is acknowledged with the applying tick.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Every_controller_moves_its_own_slot_in_the_same_world_and_is_acknowledged(int players)
    {
        var engine = Engine(players);
        var before = engine.State.World;
        for (var slot = 0; slot < players; slot++)
        {
            Assert.Null(Send(engine, slot, 1, Directions[slot].X, Directions[slot].Y));
        }
        var outcome = engine.Step();
        Assert.Equal(1, outcome.Tick);
        for (var slot = 0; slot < players; slot++)
        {
            var expected = Movement.Move(before.Players[slot].XFixed, before.Players[slot].YFixed, Directions[slot].X, Directions[slot].Y);
            var p = engine.State.World.Players[slot];
            Assert.Equal((expected.XFixed, expected.YFixed), (p.XFixed, p.YFixed));
            Assert.Contains(new AckItem($"c{slot}", slot, 1, 1, 1), outcome.Acks);
        }
        Assert.Empty(outcome.Rejections);
        Assert.Equal(players, outcome.Applied.Count);
    }

    /// <summary>
    /// **Validates: Requirements 10.03** An input queued by a controller that is superseded (takeover) or that closed BEFORE the tick
    /// applies it never affects the world: the epoch and connection are re-read at application time, not at queueing time.
    /// </summary>
    [Fact]
    public void Inputs_queued_before_a_takeover_or_disconnect_are_rejected_when_the_tick_applies_them()
    {
        var engine = Engine(2);
        var start = engine.State.World;
        Assert.Null(Send(engine, 0, 1, 1, 0));                       // queued by c0 at epoch 1
        Assert.Null(Send(engine, 1, 1, 1, 0));                       // queued by c1 at epoch 1
        engine.SetController(new SlotControl(0, 2, "c0-new-tab"));   // takeover after queueing
        engine.SetController(new SlotControl(1, 1, null));           // slot 1's controller closed after queueing
        var outcome = engine.Step();
        Assert.Contains(outcome.Rejections, r => r.Slot == 0 && r.Code == GhostLapFailure.Superseded);
        Assert.Contains(outcome.Rejections, r => r.Slot == 1 && r.Code == GhostLapFailure.NotConnected);
        Assert.Equal((start.Players[0].XFixed, start.Players[0].YFixed), (engine.State.World.Players[0].XFixed, engine.State.World.Players[0].YFixed));
        Assert.Equal((start.Players[1].XFixed, start.Players[1].YFixed), (engine.State.World.Players[1].XFixed, engine.State.World.Players[1].YFixed));

        // The old connection cannot pass by claiming the new epoch; the new controller can.
        Assert.Null(Send(engine, 0, 2, 1, 0, epoch: 2, connection: "c0"));
        Assert.Null(Send(engine, 0, 3, 1, 0, epoch: 2, connection: "c0-new-tab"));
        var next = engine.Step();
        Assert.Contains(next.Rejections, r => r.Seq == 2 && r.Code == GhostLapFailure.Superseded);
        Assert.Contains(next.Acks, a => a.ConnectionId == "c0-new-tab" && a.LastAppliedSeq == 3 && a.ControllerEpoch == 2);
    }

    /// <summary>
    /// **Validates: Requirements 10.06, 10.07** A connected player with no new input keeps its last direction for exactly six ticks, then
    /// stops; a disconnected player is neutralized on the very next tick (server neutral, no hold), and stays still.
    /// </summary>
    [Fact]
    public void Connected_gap_holds_six_ticks_while_a_disconnect_neutralizes_on_the_next_tick()
    {
        var engine = Engine(2);
        Assert.Null(Send(engine, 0, 1, 0, 1));
        Assert.Null(Send(engine, 1, 1, 0, 1));
        engine.Step(); // tick 1: both start moving
        engine.SetController(new SlotControl(1, 1, null)); // slot 1 disconnects; slot 0 just stops sending
        var moved0 = new List<bool>();
        var moved1 = new List<bool>();
        for (var i = 0; i < 9; i++)
        {
            var before = engine.State.World;
            var outcome = engine.Step();
            if (i == 0)
            {
                Assert.Equal([1], outcome.Neutralized);
                Assert.Contains(outcome.Applied, g => g.Slot == 1 && g is { MoveX: 0, MoveY: 0, RequestBurst: false });
            }
            else
            {
                Assert.Empty(outcome.Neutralized); // neutralized once, not every tick
            }
            // The held vector is the vector that moved the player on this tick (independent of whether a wall blocked it).
            moved0.Add(engine.State.World.Players[0].HeldMoveY != 0);
            moved1.Add(engine.State.World.Players[1].HeldMoveY != 0 || before.Players[1].XFixed != engine.State.World.Players[1].XFixed
                       || before.Players[1].YFixed != engine.State.World.Players[1].YFixed);
        }
        // Ticks 2..6 still use slot 0's held vector (hold until tick 1 + 6 = 7), tick 7 included; from tick 8 it is neutral.
        Assert.Equal([true, true, true, true, true, true, false, false, false], moved0);
        Assert.All(moved1, Assert.False);
    }

    /// <summary>
    /// **Validates: Requirements 10.07** Same-tick coalescing: several admitted inputs of one slot in one tick are all given to the reducer
    /// in sequence order. The highest sequence sets the direction; a press and release inside the tick is one Burst (not lost),
    /// press-release-press is still one (at most one per player per tick), and a held press on later inputs never repeats it.
    /// </summary>
    [Fact]
    public void A_press_and_release_inside_one_tick_fires_exactly_one_Burst_and_the_latest_direction_wins()
    {
        var engine = Engine(2, secret: 3);
        var driver = new CandleSeekingDriver(2);
        var v1 = GameContext.Create(engine.Context.Seed, 2);
        var seq = new int[2];
        // Real play until a slot is Overlit with its latch released.
        while (!engine.State.World.Players.Any(p => p.IsOverlit && !p.BurstLatch))
        {
            foreach (var g in driver.Inputs(engine.State.World, v1))
            {
                seq[g.Slot] = g.Sequence;
                Assert.Null(Send(engine, g.Slot, g.Sequence, g.MoveX, g.MoveY, false));
            }
            engine.Step();
            Assert.True(engine.State.Tick < GameRulesV1.RoundTicks - 10, "no slot became Overlit");
        }
        var slot = engine.State.World.Players.First(p => p.IsOverlit && !p.BurstLatch).Slot;
        var other = 1 - slot;
        Assert.Null(Send(engine, slot, ++seq[slot], 1, 0, burst: true));
        Assert.Null(Send(engine, slot, ++seq[slot], 0, 0, burst: false));
        Assert.Null(Send(engine, slot, ++seq[slot], 0, 1, burst: true));
        Assert.Null(Send(engine, slot, ++seq[slot], -1, 0, burst: false));
        var outcome = engine.Step();
        Assert.Single(engine.State.World.Events, e => e.Kind == GameEventKind.BurstManual && e.Slot == slot);
        var p = engine.State.World.Players[slot];
        Assert.Equal((-1, 0), ((int)p.HeldMoveX, (int)p.HeldMoveY));
        Assert.Contains(outcome.Acks, a => a.Slot == slot && a.LastAppliedSeq == seq[slot]);

        // A press held across later inputs gives no second Burst even if the slot became Overlit again meanwhile.
        Assert.Null(Send(engine, slot, ++seq[slot], 0, 0, burst: true));
        engine.Step();
        for (var i = 0; i < 30; i++)
        {
            Assert.Null(Send(engine, slot, ++seq[slot], 0, 0, burst: true));
            Assert.Null(Send(engine, other, ++seq[other], 0, 0));
            engine.Step();
            Assert.DoesNotContain(engine.State.World.Events, e => e.Kind == GameEventKind.BurstManual && e.Slot == slot);
        }
    }

    /// <summary>**Validates: Requirements 10.03, 10.07** Each refusal is typed and none affects the world: stale/duplicate sequence, tick window, limits, wrong match.</summary>
    [Fact]
    public void Stale_late_future_over_limit_and_foreign_inputs_are_refused_with_typed_codes()
    {
        var engine = Engine(2, new MultiplayerOptions { InputsPerSecond = 1000, InputBurst = 1000, MaxInputsPerSlotPerTick = 3, MailboxCapacity = 16 });
        engine.Step(); // tick 1
        Assert.Null(Send(engine, 0, 5, 1, 0));
        Assert.Null(Send(engine, 0, 5, -1, 0));                          // duplicate sequence
        Assert.Null(Send(engine, 0, 4, -1, 0));                          // older sequence
        Assert.Null(Send(engine, 1, 1, 1, 0, clientTick: 2 - 5));        // more than 4 ticks late
        Assert.Null(Send(engine, 1, 2, 1, 0, clientTick: 2 + 3));        // more than 2 ticks ahead
        Assert.Null(Send(engine, 1, 3, 0, 1, clientTick: 2 - 4));        // exactly at the late bound: accepted
        var outcome = engine.Step();
        Assert.Equal(
            [GhostLapFailure.StaleSequence, GhostLapFailure.StaleSequence, GhostLapFailure.LateTick, GhostLapFailure.FutureTick],
            outcome.Rejections.Select(r => r.Code));
        Assert.Equal(1, engine.State.World.Players[0].HeldMoveX);
        Assert.Equal(1, engine.State.World.Players[1].HeldMoveY);

        for (var s = 10; s < 14; s++)
        {
            Assert.Null(Send(engine, 0, s, 1, 0));
        }
        Assert.Equal([GhostLapFailure.TickInputLimit], engine.Step().Rejections.Select(r => r.Code));

        var foreign = new GameplayInput(Guid.NewGuid().ToString("N"), 1, 4, 20, 1, 0, false);
        Assert.Equal(GhostLapFailure.WrongMatch, engine.TryEnqueue(new AdmittedInput(engine.Config.RoomId, MemberOf(engine, 0), "x", 1, foreign, "c0")));
        Assert.Equal(GhostLapFailure.Forbidden, engine.TryEnqueue(new AdmittedInput(
            engine.Config.RoomId, "not-a-member", "x", 1, foreign with { MatchId = engine.Config.MatchId.ToString("N") }, "c0")));
        for (var i = 0; i < 16; i++)
        {
            Assert.Null(Send(engine, 1, 100 + i, 0, 1));
        }
        Assert.Equal(GhostLapFailure.MailboxFull, Send(engine, 1, 200, 0, 1)); // bounded: refused, never blocks
    }

    /// <summary>**Validates: Requirements 10.03** The aggregate per-slot budget refuses a burst beyond its allowance (on top of the per-connection hub budget).</summary>
    [Fact]
    public void The_per_slot_budget_bounds_the_aggregate_input_rate()
    {
        var time = new ManualTime();
        var engine = Engine(2, new MultiplayerOptions { InputsPerSecond = 30, InputBurst = 5 }, time);
        var results = Enumerable.Range(1, 8).Select(i => Send(engine, 0, i, 1, 0)).ToArray();
        Assert.Equal(5, results.Count(r => r is null));
        Assert.All(results.Skip(5), r => Assert.Equal(GhostLapFailure.RateLimited, r));
        time.Advance(TimeSpan.FromMilliseconds(100)); // 3 tokens at 30/s
        Assert.Null(Send(engine, 0, 9, 1, 0));
    }

    /// <summary>
    /// **Validates: Requirements 10.04, 10.08** An input queued before tick T is applied BY tick T (acknowledged with T); one queued after
    /// T was executed can only affect T+1. A full round resolves exactly 2700 ticks, and the applied inputs recorded from the outcomes
    /// replay through the pure reducer to the same final hash.
    /// </summary>
    [Fact]
    public void Inputs_apply_to_the_next_tick_and_a_full_round_replays_from_its_applied_inputs()
    {
        var time = new ManualTime();
        var engine = Engine(3, time: time, secret: 9);
        var driver = new CandleSeekingDriver(3);
        var v1 = GameContext.Create(engine.Context.Seed, 3);
        var applied = new List<IReadOnlyList<GameInput>>();
        while (!engine.State.IsFinal)
        {
            time.Advance(TimeSpan.FromMilliseconds(34)); // real input rate (one per tick): the per-slot budget refills
            var tick = engine.State.Tick + 1;
            foreach (var g in driver.Inputs(engine.State.World, v1))
            {
                Assert.Null(Send(engine, g.Slot, g.Sequence, g.MoveX, g.MoveY, g.RequestBurst));
            }
            var outcome = engine.Step();
            Assert.Equal(tick, outcome.Tick);
            Assert.All(outcome.Acks, a => Assert.Equal(tick, a.Tick));
            Assert.All(outcome.Applied, g => Assert.Equal(tick, g.Tick));
            Assert.Empty(outcome.Rejections);
            applied.Add(outcome.Applied);
        }
        Assert.Equal(GameRulesV1.RoundTicks, engine.State.Tick);
        Assert.Throws<InvalidOperationException>(() => engine.Step());

        var replay = MpMatchState.Initial(engine.Context);
        foreach (var inputs in applied)
        {
            replay = MpRules.Step(replay, inputs, [], engine.Context);
        }
        Assert.Equal(MpStateCodec.Hash(engine.State), MpStateCodec.Hash(replay));
        Assert.True(engine.State.World.Players.Sum(p => p.Pickups) >= 4);
    }
}
