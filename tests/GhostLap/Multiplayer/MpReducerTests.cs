using FsCheck;
using FsCheck.Fluent;
using GhostLap.Multiplayer;
using GhostLap.Rules;
using GhostLap.Simulation;
using GhostLap.Tests.Simulation;
using GhostLap.Timeline;
using Xunit;
using Xunit.Abstractions;
using static GhostLap.Tests.Simulation.GameTestKit;

namespace GhostLap.Tests.Multiplayer;

/// <summary>
/// Task 10.2: the canonical <c>ghost-mp-v1</c> reducer is the ONE ghost-v1 phase implementation plus a participation step (ADR-P1-22
/// D2). Without forfeits it must resolve every tick byte-for-byte like ghost-v1 (so there is no second physics engine and every
/// ghost-v1 ordering rule carries over); with a forfeit the slot is excluded as actor and target from every phase.
/// </summary>
public sealed class MpReducerTests(ITestOutputHelper output)
{
    private static (MpMatchState Mp, GameState V1, int Events) PlayBoth(long seed, int players, int ticks)
    {
        var v1Context = GameContext.Create(seed, players);
        var mpContext = MpMatchContext.Create(seed, players);
        var driver = new CandleSeekingDriver(players);
        var v1 = GameState.Initial(v1Context);
        var mp = MpMatchState.Initial(mpContext);
        Assert.Equal(GameStateCodec.Instance.EncodeCanonical(v1), GameStateCodec.Instance.EncodeCanonical(mp.World));
        var events = 0;
        while (v1.Tick < ticks)
        {
            var inputs = driver.Inputs(v1, v1Context);
            v1 = Step(v1, v1Context, inputs);
            mp = MpRules.Step(mp, inputs, [], mpContext);
            Assert.Equal(GameStateCodec.Instance.EncodeCanonical(v1), GameStateCodec.Instance.EncodeCanonical(mp.World));
            events += v1.Events.Length;
        }
        return (mp, v1, events);
    }

    /// <summary>
    /// **Validates: Requirements 10.04, 10.12** Full 2700-tick rounds with candle-seeking players (pickups, Overlit, manual and automatic
    /// Bursts, contact): with no forfeit the multiplayer world equals the ghost-v1 world after every tick, for 2, 3 and 4 players.
    /// </summary>
    [Theory]
    [InlineData(7L, 2)]
    [InlineData(99L, 3)]
    [InlineData(1234L, 4)]
    public void Without_forfeits_every_tick_resolves_exactly_like_ghost_v1_over_a_full_round(long seed, int players)
    {
        var (mp, v1, events) = PlayBoth(seed, players, GameRulesV1.RoundTicks);
        Assert.True(mp.IsFinal);
        Assert.Equal(GameEventKind.RoundFinished, v1.Events[^1].Kind);
        Assert.All(mp.ForfeitTicks, f => Assert.Equal(0, f));
        output.WriteLine($"seed {seed}, {players}P: {events} events, MP hash {MpStateCodec.Hash(mp)}");
    }

    /// <summary>
    /// **Validates: Requirements 10.04** Random input scripts (any vector, Burst presses, gaps that exercise the six-tick hold) for 2 to 4
    /// players: ghost-mp-v1 without forfeits and ghost-v1 produce identical bytes at every tick.
    /// </summary>
    [Fact]
    public void Random_scripts_give_identical_worlds_in_ghost_mp_v1_and_ghost_v1()
    {
        var gen = from players in Gen.Choose(2, 4)
                  from seed in Gen.Choose(0, 1_000_000)
                  from length in Gen.Choose(1, 160)
                  from cells in Gen.ArrayOf(Gen.Choose(0, 19), 4 * length)
                  select (players, seed: (long)seed, length, cells);
        Prop.ForAll(gen.ToArbitrary(), t =>
        {
            var v1Context = GameContext.Create(t.seed, t.players);
            var mpContext = MpMatchContext.Create(t.seed, t.players);
            var v1 = GameState.Initial(v1Context);
            var mp = MpMatchState.Initial(mpContext);
            var seq = new int[t.players];
            for (var tick = 1; tick <= t.length; tick++)
            {
                var inputs = new List<GameInput>();
                for (byte slot = 0; slot < t.players; slot++)
                {
                    var c = t.cells[((tick - 1) * 4) + slot];
                    if (c >= 18)
                    {
                        continue; // no input this tick: held vector or expiry
                    }
                    inputs.Add(new GameInput(tick, slot, ++seq[slot], (sbyte)((c % 3) - 1), (sbyte)(((c / 3) % 3) - 1), c >= 9));
                }
                v1 = Step(v1, v1Context, [.. inputs]);
                mp = MpRules.Step(mp, inputs, [], mpContext);
                if (!GameStateCodec.Instance.EncodeCanonical(v1).AsSpan().SequenceEqual(GameStateCodec.Instance.EncodeCanonical(mp.World)))
                {
                    return false;
                }
            }
            return true;
        }).Check(Config.QuickThrowOnFailure.WithMaxTest(150));
    }

    /// <summary>**Validates: Requirements 10.04** Same seed and the same applied inputs give the same ghost-mp-state/1 hash in two independent runs.</summary>
    [Theory]
    [InlineData(5L, 2)]
    [InlineData(6L, 4)]
    public void Same_seed_and_applied_inputs_reproduce_the_same_state_hash(long seed, int players)
    {
        var a = PlayBoth(seed, players, 900).Mp;
        var b = PlayBoth(seed, players, 900).Mp;
        Assert.Equal(MpStateCodec.Hash(a), MpStateCodec.Hash(b));
        Assert.Matches("^[0-9a-f]{64}$", MpStateCodec.Hash(a));
        // A different seed is a different world.
        Assert.NotEqual(MpStateCodec.Hash(a), MpStateCodec.Hash(PlayBoth(seed + 1, players, 900).Mp));
        // The forfeit vector is part of the hashed state.
        Assert.NotEqual(MpStateCodec.Hash(a), MpStateCodec.Hash(a with { ForfeitTicks = [900, .. a.ForfeitTicks.Skip(1)] }));
    }

    /// <summary>
    /// **Validates: Requirements 10.06** (reducer level; scheduling the forfeit after 450 grace ticks is task 10.3). A forfeited slot is
    /// excluded as actor and as target: it does not move, cannot collect a candle it stands on, is in no contact pair, cannot fire a
    /// manual or automatic Burst (its pending Overlit is cleared without one) and cannot be hit. Score and charge stay frozen.
    /// </summary>
    [Fact]
    public void A_forfeited_slot_takes_no_part_in_any_phase_and_its_pending_auto_Burst_never_fires()
    {
        var ctx = MpMatchContext.Create(42, 2);
        var candle = ctx.Plan.Candles.First();
        var tick = candle.ActivateTick;
        // Slot 0 is Overlit with its deadline on this tick and stands on the active candle; slot 1 is 60 u away: inside the Burst
        // radius, outside contact and pickup range.
        var initial = MpMatchState.Initial(ctx).World;
        var overlit = initial.Players[0] with { XFixed = candle.Site.X, YFixed = candle.Site.Y, Charge = 4, OverlitDeadlineTick = tick, DistanceFixed = 50_000 };
        var rival = initial.Players[1] with { XFixed = candle.Site.X + U(60), YFixed = candle.Site.Y, Charge = 1, DistanceFixed = 900_000 };
        var world = initial with { Tick = tick - 1, ActiveCandleIds = ctx.Plan.ActiveIdsAt(tick - 1), Players = [overlit, rival] };
        var start = new MpMatchState(world, [0, 0]);

        // Control: without the forfeit, the same tick collects nothing more (full charge), auto-Bursts and hits slot 1.
        var control = MpRules.Step(start, [], [], ctx);
        Assert.Contains(control.World.Events, e => e.Kind == GameEventKind.BurstAuto && e.Slot == 0);
        Assert.Contains(control.World.Events, e => e.Kind == GameEventKind.BurstHit && e.Slot == 1);

        var forfeited = MpRules.Step(start, [], [0], ctx);
        Assert.Equal(tick, forfeited.ForfeitTicks[0]);
        Assert.Contains(candle.Id, forfeited.World.ActiveCandleIds); // standing on it collects nothing
        Assert.DoesNotContain(forfeited.World.Events, e => e.Kind is GameEventKind.BurstAuto or GameEventKind.BurstManual or GameEventKind.BurstHit);
        Assert.DoesNotContain(forfeited.World.Events, e => e.Slot == 0 || (e.Kind == GameEventKind.ContactDrain && e.OtherSlot == 0));
        var p0 = forfeited.World.Players[0];
        Assert.Equal((overlit.XFixed, overlit.YFixed, overlit.DistanceFixed, 4, 0L), (p0.XFixed, p0.YFixed, p0.DistanceFixed, p0.Charge, p0.OverlitDeadlineTick));
        Assert.Equal(overlit.TotalScore, p0.TotalScore);
        Assert.Equal(rival.TotalScore, forfeited.World.Players[1].TotalScore);

        // The forfeited slot collects nothing on later ticks either, even standing on a candle, and a candle it stood on stays for others.
        var later = forfeited;
        for (var i = 0; i < 40; i++)
        {
            later = MpRules.Step(later, [new GameInput(later.Tick + 1, 1, i + 1, -1, 0, false)], [], ctx);
        }
        Assert.Equal(overlit.Pickups, later.World.Players[0].Pickups);
        Assert.Equal((overlit.XFixed, overlit.YFixed), (later.World.Players[0].XFixed, later.World.Players[0].YFixed));
        Assert.All(later.World.Collected, c => Assert.NotEqual(0, c.Slot));

        // The reducer refuses an input for a forfeited slot (the match loop never passes one).
        Assert.Throws<GhostLapTransitionException>(() => MpRules.Step(forfeited, [new GameInput(tick + 1, 0, 1, 1, 0, false)], [], ctx));
    }

    /// <summary>**Validates: Requirements 10.12** A sealed config naming another rules/layout identity or spawn assignment is refused, never reinterpreted.</summary>
    [Fact]
    public void A_sealed_round_for_another_identity_is_refused()
    {
        var config = MatchTestKit.Seal(3);
        Assert.Equal(3, MpMatchContext.From(config).PlayerCount);
        Assert.Throws<ArgumentException>(() => MpMatchContext.From(config with { RulesId = GameRulesV1.RulesVersion }));
        Assert.Throws<ArgumentException>(() => MpMatchContext.From(config with { TargetTicks = 2701 }));
        Assert.Throws<ArgumentException>(() => MpMatchContext.From(config with { LayoutFingerprint = new string('0', 64) }));
        Assert.Throws<ArgumentException>(() => MpMatchContext.From(config with
        {
            Slots = [config.Slots[1] with { Slot = 0 }, config.Slots[0] with { Slot = 1 }, config.Slots[2]],
        }));
    }
}
