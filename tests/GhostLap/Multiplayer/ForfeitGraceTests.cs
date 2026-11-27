using GhostLap.Contracts.Multiplayer;
using GhostLap.Multiplayer;
using GhostLap.Rules;
using GhostLap.Simulation;
using Xunit;
using static GhostLap.Tests.Multiplayer.MatchTestKit;

namespace GhostLap.Tests.Multiplayer;

/// <summary>
/// Task 10.3: the 450-tick grace and the forfeit, driven through the production <see cref="MatchEngine"/> (controller read once per tick, server
/// neutral, phase 0 forfeit, the shared ghost-v1 phases). D is the tick at which the loop first observes the missing controller; ticks D..D+449 are
/// grace and the slot is forfeited in phase 0 of tick F = D + 450 unless a controller is connected when F begins (ADR-P1-22 D5/D6).
/// </summary>
public sealed class ForfeitGraceTests
{
    private static long U(int units) => units * (long)GameRulesV1.FixedOne;

    private static void StepTo(MatchEngine engine, long tick)
    {
        while (engine.State.Tick < tick)
        {
            engine.Step();
        }
    }

    /// <summary>A spot where two colliders 60 u apart can stand and no candle site is anywhere near, so only the Burst and the forfeit act.</summary>
    private static (int X, int Y) QuietSpot()
    {
        for (var y = 200; y <= 700; y += 20)
        {
            for (var x = 200; x <= 1300; x += 20)
            {
                var a = (X: (int)U(x), Y: (int)U(y));
                var b = (X: (int)U(x + 60), Y: a.Y);
                if (ArenaV1.CanOccupy(a.X, a.Y) && ArenaV1.CanOccupy(b.X, b.Y)
                    && ArenaV1.CandleSites.All(c => Math.Max(Math.Abs((long)c.X - a.X), Math.Abs((long)c.Y - a.Y)) > U(120)
                                                    && Math.Max(Math.Abs((long)c.X - b.X), Math.Abs((long)c.Y - b.Y)) > U(120)))
                {
                    return a;
                }
            }
        }
        throw new InvalidOperationException("No quiet spot in arena-v1.");
    }

    /// <summary>Slot 0 Overlit (deadline given) 60 u from slot 1, both idle, starting after tick 100. Slot 0 has no controller; slot 1 stays connected.</summary>
    private static MatchEngine OverlitEngine(long deadline, bool latch = false)
    {
        var config = Seal(2);
        var context = MpMatchContext.From(config);
        var world = MpMatchState.Initial(context).World;
        var (x, y) = QuietSpot();
        var p0 = world.Players[0] with { XFixed = x, YFixed = y, Charge = 4, OverlitDeadlineTick = deadline, DistanceFixed = 50_000, BurstLatch = latch };
        var p1 = world.Players[1] with { XFixed = x + (int)U(60), YFixed = y, Charge = 1, DistanceFixed = 900_000 };
        var start = new MpMatchState(world with { Tick = 100, ActiveCandleIds = context.Plan.ActiveIdsAt(100), Players = [p0, p1] }, [0, 0]);
        var options = new MultiplayerOptions { InputsPerSecond = 1000, InputBurst = 1000 };
        var engine = new MatchEngine(config, options, new ManualTime(), start);
        engine.SetController(new SlotControl(0, 1, "c0"));
        engine.SetController(new SlotControl(1, 1, "c1"));
        return engine;
    }

    /// <summary>**Validates: Requirements 10.06** Grace is exactly ticks D..D+449; the forfeit is applied in phase 0 of D+450, with its event first.</summary>
    [Fact]
    public void Grace_covers_D_through_D_plus_449_and_the_forfeit_is_applied_in_phase_0_of_D_plus_450()
    {
        var engine = Engine(2);
        StepTo(engine, 100);
        engine.SetController(new SlotControl(1, 1, null)); // observed at the read of tick 101: D = 101
        var first = engine.Step();
        Assert.Equal([1], first.Neutralized);
        Assert.Equal(101, first.Tick);
        Assert.Equal(new SlotPresence(false, 551), first.Presence[1]);
        for (var tick = 102; tick <= 550; tick++) // D+449 = 550 is the last grace tick
        {
            var o = engine.Step();
            Assert.Empty(o.Forfeited);
            Assert.False(engine.State.IsForfeited(1), $"forfeited early at {tick}");
            Assert.Empty(o.Neutralized);
            Assert.Equal(new SlotPresence(false, 551), o.Presence[1]);
        }
        var f = engine.Step(); // tick 551 = D + 450
        Assert.Equal(551, f.Tick);
        Assert.Equal([1], f.Forfeited);
        Assert.Equal(551, engine.State.ForfeitTicks[1]);
        Assert.Equal(0, engine.State.ForfeitTicks[0]);
        var forfeit = f.Events[0];
        Assert.Equal((551L, 0, MatchEventKinds.Forfeited, 1), (forfeit.Tick, forfeit.Index, forfeit.Kind, forfeit.Slot));
        Assert.Equal(new SlotPresence(false, 0), f.Presence[1]);
        Assert.Equal(engine.State.World.Players[1].TotalScore, forfeit.Value);
    }

    /// <summary>
    /// **Validates: Requirements 10.06** A controller present when tick F begins keeps the slot (a takeover processed before F resolves wins); one
    /// that arrives after F resolved cannot undo it.
    /// </summary>
    [Theory]
    [InlineData(450, false)] // before D+449 resolved
    [InlineData(451, false)] // present at the read of F itself: kept
    [InlineData(452, true)]  // one tick late: already forfeited at 451
    public void A_takeover_before_tick_F_keeps_the_slot_and_one_after_is_spectator_only(int reconnectBeforeTick, bool forfeited)
    {
        var engine = Engine(2);
        engine.SetController(new SlotControl(1, 1, null)); // D = 1, F = 451
        StepTo(engine, reconnectBeforeTick - 1);
        engine.SetController(new SlotControl(1, 2, "c1-new"));
        StepTo(engine, 455);
        Assert.Equal(forfeited, engine.State.IsForfeited(1));
        if (forfeited)
        {
            Assert.Equal(451, engine.State.ForfeitTicks[1]);
            // The late controller is refused for good, with the typed reason and the floor to resume from.
            Assert.Null(Send(engine, 1, 100, 1, 0, epoch: 2, connection: "c1-new"));
            var outcome = engine.Step();
            var rejection = Assert.Single(outcome.Rejections);
            Assert.Equal((GhostLapFailure.Forfeited, 1), (rejection.Code, rejection.Slot));
            Assert.Empty(outcome.Acks);
        }
        else
        {
            // Kept: the new controller moves the slot again, and a later loss starts a NEW 450-tick grace.
            Assert.Null(Send(engine, 1, 100, 1, 0, epoch: 2, connection: "c1-new"));
            var ack = Assert.Single(engine.Step().Acks);
            Assert.Equal((1, 100), (ack.Slot, ack.LastAppliedSeq));
            engine.SetController(new SlotControl(1, 2, null));
            var lossTick = engine.State.Tick + 1;
            StepTo(engine, lossTick + 448);
            Assert.False(engine.State.IsForfeited(1));
            StepTo(engine, lossTick + 450);
            Assert.Equal(lossTick + 450, engine.State.ForfeitTicks[1]);
        }
    }

    /// <summary>
    /// **Validates: Requirements 10.06** Over many random connect/disconnect scripts the forfeit tick equals the first tick at which the slot has been
    /// without a controller for 451 consecutive reads (D..D+450), computed independently from the script; the same script gives the same state hash.
    /// </summary>
    [Fact]
    public void Forfeit_ticks_follow_the_script_exactly_and_replay_deterministically()
    {
        var forfeitedScripts = 0;
        for (var seed = 1; seed <= 80; seed++)
        {
            var random = new Random(seed);
            var connected = new bool[1300]; // connected[t] = controller present when tick t is read (index 0 unused)
            var present = true;
            for (var t = 1; t < connected.Length; t++)
            {
                present = present ? random.NextDouble() > 0.003 : random.NextDouble() < 0.004;
                connected[t] = present;
            }
            long expected = 0;
            var run = 0;
            for (var t = 1; t < connected.Length && expected == 0; t++)
            {
                run = connected[t] ? 0 : run + 1;
                if (run == 451)
                {
                    expected = t;
                }
            }
            forfeitedScripts += expected != 0 ? 1 : 0;
            string? hash = null;
            for (var pass = 0; pass < 2; pass++)
            {
                var engine = Engine(2, secret: (byte)seed);
                long epoch = 1;
                for (var t = 1; t < connected.Length && !engine.State.IsFinal; t++)
                {
                    var was = engine.ControllerOf(1).ConnectionId is not null;
                    if (connected[t] != was)
                    {
                        engine.SetController(new SlotControl(1, connected[t] ? ++epoch : epoch, connected[t] ? $"c1-{epoch}" : null));
                    }
                    engine.Step();
                }
                Assert.Equal(expected, engine.State.ForfeitTicks[1]);
                Assert.Equal(0, engine.State.ForfeitTicks[0]);
                var current = MpStateCodec.Hash(engine.State);
                Assert.True(hash is null || hash == current, $"seed {seed}: replay differs");
                hash = current;
            }
        }
        Assert.InRange(forfeitedScripts, 8, 72); // the scripts exercise both outcomes
    }

    /// <summary>**Validates: Requirements 10.06** After the forfeit the slot is frozen, takes no part and the round still ends normally at tick 2700.</summary>
    [Fact]
    public void A_forfeited_slot_is_frozen_while_the_others_play_on_to_tick_2700()
    {
        var time = new ManualTime();
        var engine = Engine(3, new MultiplayerOptions { InputsPerSecond = 1000, InputBurst = 1000 }, time, secret: 5);
        engine.SetController(new SlotControl(2, 1, null)); // D = 1, forfeited at 451
        StepTo(engine, 451);
        var frozen = engine.State.World.Players[2];
        var score = frozen.TotalScore;
        var seq = 0;
        while (!engine.State.IsFinal)
        {
            time.Advance(TimeSpan.FromMilliseconds(34));
            Assert.Null(Send(engine, 0, ++seq, engine.State.Tick % 60 < 30 ? 1 : -1, 0));
            engine.Step();
            var p = engine.State.World.Players[2];
            Assert.Equal((frozen.XFixed, frozen.YFixed, frozen.DistanceFixed, frozen.Charge, frozen.Pickups, score), (p.XFixed, p.YFixed, p.DistanceFixed, p.Charge, p.Pickups, p.TotalScore));
            Assert.Equal(0, p.OverlitDeadlineTick);
            Assert.DoesNotContain(
                engine.State.World.Events,
                e => e.Kind != GameEventKind.RoundFinished && (e.Slot == 2 || (e.Kind is GameEventKind.ContactDrain or GameEventKind.BurstHit && e.OtherSlot == 2)));
        }
        Assert.Equal(GameRulesV1.RoundTicks, engine.State.Tick);
        Assert.Equal(451, engine.State.ForfeitTicks[2]);
    }

    /// <summary>
    /// **Validates: Requirements 10.06, 10.07** A grace-period slot is still a normal participant: its pending Overlit auto-Burst fires on its deadline
    /// through tick D+449; from F on nothing fires, the deadline is cleared and the charge is only a frozen statistic. The tick F itself is
    /// forfeited first (phase 0), so a deadline equal to F does not fire.
    /// </summary>
    [Theory]
    [InlineData(300, true)]   // inside the grace
    [InlineData(550, true)]   // D + 449: the last grace tick
    [InlineData(551, false)]  // = F: phase 0 forfeits before the auto-Burst phase
    [InlineData(600, false)]  // after the forfeit
    public void The_auto_Burst_fires_during_grace_and_never_after_the_forfeit(long deadline, bool fires)
    {
        var engine = OverlitEngine(deadline);
        engine.SetController(new SlotControl(0, 1, null)); // observed at tick 101: D = 101, F = 551
        var burstEvents = new List<GameEvent>();
        StepTo(engine, 100);
        while (engine.State.Tick < 620)
        {
            engine.Step();
            burstEvents.AddRange(engine.State.World.Events.Where(e => e.Kind is GameEventKind.BurstAuto or GameEventKind.BurstHit));
        }
        Assert.Equal(551, engine.State.ForfeitTicks[0]);
        if (fires)
        {
            Assert.Contains(burstEvents, e => e is { Kind: GameEventKind.BurstAuto, Slot: 0 });
            Assert.Contains(burstEvents, e => e is { Kind: GameEventKind.BurstHit, Slot: 1 });
            Assert.Equal(0, engine.State.World.Players[0].Charge); // the Burst reset it, and the frozen stat stays 0
            Assert.True(engine.State.World.Players[1].PenaltyPoints > 0);
        }
        else
        {
            Assert.Empty(burstEvents);
            var p0 = engine.State.World.Players[0];
            Assert.Equal((4, 0L), (p0.Charge, p0.OverlitDeadlineTick)); // kept only as a frozen stat, its deadline cleared without a Burst
            Assert.Equal(0, engine.State.World.Players[1].PenaltyPoints);
        }
    }

    /// <summary>
    /// **Validates: Requirements 10.06, 10.07** (ADR D7) The disconnect neutral releases the Burst latch, so a key held through a reconnect gives one
    /// rising edge on the first accepted input: exactly one Burst, never a repeat from the held key, a duplicate or a late older item.
    /// </summary>
    [Fact]
    public void A_reconnect_gives_one_Burst_edge_and_duplicates_or_reordered_items_never_repeat_it()
    {
        var engine = OverlitEngine(deadline: 2000, latch: true);
        engine.SetController(new SlotControl(0, 1, null));
        engine.Step(); // tick 101: the server neutral releases the latch
        Assert.False(engine.State.World.Players[0].BurstLatch);
        var neutralSeq = engine.State.World.Players[0].LastSequence;
        Assert.True(neutralSeq >= 0);
        engine.SetController(new SlotControl(0, 2, "c0-new"));
        // The new tab presses Burst; the same item is delivered twice and an older one arrives late (reordered retransmissions).
        Assert.Null(Send(engine, 0, neutralSeq + 2, 0, 0, burst: true, epoch: 2, connection: "c0-new"));
        Assert.Null(Send(engine, 0, neutralSeq + 2, 0, 0, burst: true, epoch: 2, connection: "c0-new")); // duplicate
        Assert.Null(Send(engine, 0, neutralSeq + 1, 0, 0, burst: true, epoch: 2, connection: "c0-new")); // reordered older
        var outcome = engine.Step();
        Assert.Single(engine.State.World.Events, e => e.Kind == GameEventKind.BurstManual && e.Slot == 0);
        Assert.Equal([GhostLapFailure.StaleSequence, GhostLapFailure.StaleSequence], outcome.Rejections.Select(r => r.Code));
        Assert.All(outcome.Rejections, r => Assert.Equal(neutralSeq + 2, r.SequenceFloor));
        Assert.Equal(neutralSeq + 2, Assert.Single(outcome.Acks).LastAppliedSeq);
        for (var i = 0; i < 20; i++) // the key stays held: no further edge (and the charge is spent anyway)
        {
            Assert.Null(Send(engine, 0, neutralSeq + 3 + i, 0, 0, burst: true, epoch: 2, connection: "c0-new"));
            engine.Step();
            Assert.DoesNotContain(engine.State.World.Events, e => e.Kind == GameEventKind.BurstManual);
        }
    }

    /// <summary>
    /// **Validates: Requirements 10.07** Key release during the six-tick hold: a connected slot keeps its last direction for six ticks then stops, a
    /// newer legal item replaces it at once, and a stale item cannot renew the hold.
    /// </summary>
    [Fact]
    public void A_stale_item_cannot_renew_the_six_tick_hold_and_a_newer_one_replaces_it()
    {
        var engine = Engine(2);
        Assert.Null(Send(engine, 0, 5, 0, 1));
        engine.Step(); // tick 1 moves, hold until tick 7
        var held = new List<bool>();
        for (var i = 0; i < 8; i++)
        {
            Assert.Null(Send(engine, 0, 5, 0, 1)); // retransmission of the same item: stale, must not renew
            var o = engine.Step();
            held.Add(engine.State.World.Players[0].HeldMoveY != 0);
            Assert.Equal([GhostLapFailure.StaleSequence], o.Rejections.Select(r => r.Code));
        }
        Assert.Equal([true, true, true, true, true, true, false, false], held);
        Assert.Null(Send(engine, 0, 6, 1, 0));
        engine.Step();
        Assert.Equal((1, 0), ((int)engine.State.World.Players[0].HeldMoveX, (int)engine.State.World.Players[0].HeldMoveY));
    }

    /// <summary>
    /// **Validates: Requirements 10.06, 10.07, 10.12** The ghost-mp-net-v0 timing constants and the grace are pinned: the input window (4 ticks behind, 2 ahead),
    /// the six-tick hold, the 450-tick grace and the 2-tick snapshot interval. A change is a new rules/net version, never a silent edit.
    /// </summary>
    [Fact]
    public void The_network_profile_and_grace_constants_are_pinned()
    {
        Assert.Equal((4, 2, 6), (GameRulesV1.InputMaxLateTicks, GameRulesV1.InputMaxLeadTicks, GameRulesV1.InputHoldTicks));
        Assert.Equal(450, MpRulesProfile.GraceTicks);
        Assert.Equal((30, 2700), (GameRulesV1.TickRate, GameRulesV1.RoundTicks));
        var defaults = new MultiplayerOptions();
        Assert.Equal(2, defaults.SnapshotIntervalTicks);
        Assert.Empty(MultiplayerStartup.Validate(defaults));
        Assert.NotEmpty(MultiplayerStartup.Validate(new MultiplayerOptions { HubKeepAliveSeconds = 10, HubClientTimeoutSeconds = 15 }));
    }
}
