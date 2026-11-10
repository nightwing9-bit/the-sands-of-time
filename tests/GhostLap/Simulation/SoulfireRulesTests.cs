using FsCheck;
using FsCheck.Fluent;
using GhostLap.Rules;
using GhostLap.Simulation;
using GhostLap.Timeline;
using Xunit;
using static GhostLap.Tests.Simulation.GameTestKit;

namespace GhostLap.Tests.Simulation;

/// <summary>Charge, Overlit, contact, Burst, score clamps and finish of <c>ghost-v1</c> (task 6.2).</summary>
public sealed class SoulfireRulesTests
{
    // A clear vertical lane at x = 120: no obstacle, no candle site within pickup reach.
    private static readonly int LaneX = U(120);

    [Fact]
    public void Pickup_scores_25_adds_one_charge_and_each_candle_is_collected_once_per_world()
    {
        var ctx = GameContext.Create(42, 1);
        var c = CandleActiveAt(ctx, 100);
        var s1 = Step(At(ctx, 99, Player(0, c.Site.X, c.Site.Y)), ctx);
        var p = s1.Players[0];
        Assert.Equal((1, 1, 25, 25), (p.Charge, p.Pickups, p.PickupPoints, p.TotalScore));
        Assert.DoesNotContain(c.Id, s1.ActiveCandleIds);
        Assert.Contains(new CollectedCandle(c.Id, 0, 100), s1.Collected);
        Assert.Equal(new[] { new GameEvent(GameEventKind.CandleCollected, 0, 0, 25, c.Id) }, s1.Events);

        var s2 = Step(s1, ctx); // still standing on the site while the candle's planned window continues
        Assert.True(c.ExpireTick > 101);
        Assert.Equal(1, s2.Players[0].Pickups);
        Assert.DoesNotContain(c.Id, s2.ActiveCandleIds);
        Assert.Empty(EventsOf(s2, GameEventKind.CandleCollected));
    }

    [Fact]
    public void A_contested_candle_goes_to_the_lowest_slot()
    {
        var ctx = GameContext.Create(42, 2);
        var c = CandleActiveAt(ctx, 100);
        var s1 = Step(At(ctx, 99, Player(0, c.Site.X, c.Site.Y), Player(1, c.Site.X, c.Site.Y)), ctx);
        Assert.Equal(1, s1.Players[0].Pickups);
        Assert.Equal(0, s1.Players[1].Pickups);
        Assert.Contains(new CollectedCandle(c.Id, 0, 100), s1.Collected);
    }

    [Fact]
    public void Reaching_four_starts_Overlit_and_a_pickup_while_full_scores_without_resetting_the_deadline()
    {
        var ctx = GameContext.Create(42, 1);
        var c = CandleActiveAt(ctx, 100);
        var s1 = Step(At(ctx, 99, Player(0, c.Site.X, c.Site.Y, charge: 3)), ctx);
        Assert.Equal((4, 100L + GameRulesV1.OverlitTicks), (s1.Players[0].Charge, s1.Players[0].OverlitDeadlineTick));
        Assert.Equal(new[] { GameEventKind.CandleCollected, GameEventKind.OverlitStarted }, s1.Events.Select(e => e.Kind));

        var d = ctx.Plan.ActiveAt(150).First(x => x.Id != c.Id);
        var full = s1 with { Tick = 149, Players = [s1.Players[0] with { XFixed = d.Site.X, YFixed = d.Site.Y }] };
        var s2 = Step(full, ctx);
        Assert.Equal((4, 2, 50, 340L), (s2.Players[0].Charge, s2.Players[0].Pickups, s2.Players[0].PickupPoints, s2.Players[0].OverlitDeadlineTick));
        Assert.Empty(EventsOf(s2, GameEventKind.OverlitStarted));
    }

    [Fact]
    public void Unused_Overlit_auto_Bursts_exactly_on_the_deadline_and_a_solo_Burst_still_consumes_the_charge()
    {
        var ctx = GameContext.Create(42, 1);
        Assert.True(ClearSpot(LaneX, U(300)));
        var early = Step(At(ctx, 198, Player(0, LaneX, U(300), charge: 4, deadline: 200)), ctx);
        Assert.Empty(early.Events);
        Assert.True(early.Players[0].IsOverlit);

        var s = Step(early, ctx);
        Assert.Equal(new[] { new GameEvent(GameEventKind.BurstAuto, 0, 0, 0) }, s.Events);
        var p = s.Players[0];
        Assert.Equal((0, 0L, 1, 0, 0), (p.Charge, p.OverlitDeadlineTick, p.Bursts, p.BurstHits, p.TotalScore));
    }

    [Fact]
    public void Manual_request_on_the_expiry_tick_produces_exactly_one_Burst()
    {
        var ctx = GameContext.Create(42, 1);
        var s0 = At(ctx, 199, Player(0, LaneX, U(300), charge: 4, deadline: 200));
        var s = Step(s0, ctx, In(s0, 0, 1, 0, 0, burst: true));
        Assert.Equal(new[] { new GameEvent(GameEventKind.BurstManual, 0, 0, 0) }, s.Events);
        Assert.Equal(1, s.Players[0].Bursts);
    }

    [Fact]
    public void Burst_is_edge_triggered_and_a_held_request_never_refires()
    {
        var ctx = GameContext.Create(42, 1);
        PlayerState Rearm(GameState s) => s.Players[0] with { Charge = 4, OverlitDeadlineTick = s.Tick + 100 };
        var s0 = At(ctx, 10, Player(0, LaneX, U(300), charge: 4, deadline: 300));

        var s1 = Step(s0, ctx, In(s0, 0, 1, 0, 0, burst: true));
        Assert.Single(EventsOf(s1, GameEventKind.BurstManual));
        Assert.True(s1.Players[0].BurstLatch);

        var armed = s1 with { Players = [Rearm(s1)] };
        var s2 = Step(armed, ctx, In(armed, 0, 2, 0, 0, burst: true)); // still held
        Assert.Empty(s2.Events);
        var s3 = Step(s2, ctx); // no input at all: latch stays, no refire
        Assert.Empty(s3.Events);
        var s4 = Step(s3, ctx, In(s3, 0, 3, 0, 0, burst: false)); // released
        Assert.Empty(s4.Events);
        Assert.False(s4.Players[0].BurstLatch);
        var s5 = Step(s4, ctx, In(s4, 0, 4, 0, 0, burst: true)); // pressed again
        Assert.Single(EventsOf(s5, GameEventKind.BurstManual));

        // Press and release inside one tick still counts as one press.
        var armed2 = s5 with { Players = [Rearm(s5) with { BurstLatch = false }] };
        var s6 = Step(armed2, ctx, In(armed2, 0, 5, 0, 0, burst: true), In(armed2, 0, 6, 0, 0, burst: false));
        Assert.Single(EventsOf(s6, GameEventKind.BurstManual));
        Assert.False(s6.Players[0].BurstLatch);
    }

    [Fact]
    public void A_request_below_full_charge_is_consumed_without_a_Burst()
    {
        var ctx = GameContext.Create(42, 1);
        var s0 = At(ctx, 10, Player(0, LaneX, U(300), charge: 2));
        var s1 = Step(s0, ctx, In(s0, 0, 1, 0, 0, burst: true));
        Assert.Empty(s1.Events);
        Assert.Equal((2, 0), (s1.Players[0].Charge, s1.Players[0].Bursts));
    }

    [Fact]
    public void Burst_hits_within_160_units_inclusive_costs_two_charge_and_90_points_clamped_with_no_attacker_reward()
    {
        var ctx = GameContext.Create(42, 4);
        var unit = GameRulesV1.MovementScoreUnitFixed;
        var spots = new[] { (LaneX, U(300)), (LaneX, U(460)), (LaneX, U(461)), (U(200), U(300)) };
        Assert.All(spots, s => Assert.True(ClearSpot(s.Item1, s.Item2)));
        var s0 = At(ctx, 500,
            Player(0, LaneX, U(300), charge: 4, deadline: 600),
            Player(1, LaneX, U(460), charge: 3, distance: 200L * unit), // exactly 160 away
            Player(2, LaneX, U(461), charge: 3, distance: 200L * unit), // 161 away
            Player(3, U(200), U(300), charge: 1, distance: 30L * unit)); // only 30 points to lose
        var s = Step(s0, ctx, In(s0, 0, 1, 0, 0, burst: true));

        Assert.Equal(
            new[]
            {
                new GameEvent(GameEventKind.BurstManual, 0, 0, 2),
                new GameEvent(GameEventKind.BurstHit, 1, 0, 90),
                new GameEvent(GameEventKind.BurstHit, 3, 0, 30),
            },
            s.Events);
        Assert.Equal((0, 1, 2, 0), (s.Players[0].Charge, s.Players[0].Bursts, s.Players[0].BurstHits, s.Players[0].TotalScore));
        Assert.Equal((1, 110), (s.Players[1].Charge, s.Players[1].TotalScore));
        Assert.Equal((3, 200), (s.Players[2].Charge, s.Players[2].TotalScore));
        Assert.Equal((0, 0, 30), (s.Players[3].Charge, s.Players[3].TotalScore, s.Players[3].PenaltyPoints));
    }

    [Fact]
    public void A_Burst_that_cancels_a_later_slots_Overlit_stops_that_slots_same_tick_Burst()
    {
        var ctx = GameContext.Create(42, 2);
        var s0 = At(ctx, 500,
            Player(0, LaneX, U(300), charge: 4, deadline: 700),
            Player(1, LaneX, U(400), charge: 4, deadline: 501)); // would also auto-Burst this tick
        var s = Step(s0, ctx, In(s0, 0, 1, 0, 0, burst: true), In(s0, 1, 1, 0, 0, burst: true));
        Assert.Equal(
            new[]
            {
                new GameEvent(GameEventKind.BurstManual, 0, 0, 1),
                new GameEvent(GameEventKind.BurstHit, 1, 0, 0),
                new GameEvent(GameEventKind.OverlitCancelled, 1),
            },
            s.Events);
        Assert.Equal((2, 0L, 0), (s.Players[1].Charge, s.Players[1].OverlitDeadlineTick, s.Players[1].Bursts));

        // A later return to four re-arms a fresh window.
        var c = CandleActiveAt(ctx, 600);
        var back = s with { Tick = 599, Players = [s.Players[0], s.Players[1] with { Charge = 3, XFixed = c.Site.X, YFixed = c.Site.Y }] };
        var rearmed = Step(back, ctx);
        Assert.Equal(600L + GameRulesV1.OverlitTicks, rearmed.Players[1].OverlitDeadlineTick);
    }

    [Fact]
    public void Contact_drains_the_weaker_by_one_without_score_once_per_pair_per_45_ticks()
    {
        var ctx = GameContext.Create(42, 2);
        Assert.True(ClearSpot(LaneX, U(320)));
        var s = At(ctx, 100, Player(0, LaneX, U(300), charge: 3), Player(1, LaneX, U(320), charge: 2));
        var drains = new List<(long Tick, GameEvent Event)>();
        for (var i = 0; i < 100; i++)
        {
            s = Step(s, ctx);
            drains.AddRange(EventsOf(s, GameEventKind.ContactDrain).Select(e => (s.Tick, e)));
        }
        Assert.Equal(
            new[]
            {
                (101L, new GameEvent(GameEventKind.ContactDrain, 1, 0, 1)),
                (146L, new GameEvent(GameEventKind.ContactDrain, 1, 0, 1)),
                (191L, new GameEvent(GameEventKind.ContactDrain, 1, 0, 0)),
            },
            drains);
        Assert.Equal((3, 0), (s.Players[0].Charge, s.Players[1].Charge));
        Assert.All(s.Players, p => Assert.Equal(0, p.TotalScore));
    }

    [Fact]
    public void Equal_charge_contact_has_no_effect_and_starts_no_cooldown()
    {
        var ctx = GameContext.Create(42, 2);
        var s0 = At(ctx, 100, Player(0, LaneX, U(300), charge: 2), Player(1, LaneX, U(320), charge: 2));
        var s1 = Step(s0, ctx);
        Assert.Empty(s1.Events);
        Assert.Equal(new[] { 0L }, s1.PairContactReadyTicks);
    }

    [Fact]
    public void Final_tick_resolves_a_last_tick_auto_Burst_before_the_result_and_the_highest_total_wins()
    {
        var ctx = GameContext.Create(42, 2);
        var unit = GameRulesV1.MovementScoreUnitFixed;
        var s0 = At(ctx, GameRulesV1.RoundTicks - 1,
            Player(0, LaneX, U(300), charge: 4, deadline: GameRulesV1.RoundTicks, distance: 100L * unit),
            Player(1, LaneX, U(400), distance: 100L * unit));
        Assert.Null(s0.FinalResult);
        var s = Step(s0, ctx);
        Assert.Equal(
            new[] { GameEventKind.BurstAuto, GameEventKind.BurstHit, GameEventKind.RoundFinished },
            s.Events.Select(e => e.Kind));
        var result = s.FinalResult!;
        Assert.Equal(new byte[] { 0 }, result.Winners);
        Assert.False(result.IsTie);
        Assert.Equal(new PlayerScore(1, 100, 0, 90, 10, 0, 0, 0), result.Scores[1]);
        Assert.Equal(100, s.Events[^1].Value);
    }

    [Fact]
    public void Equal_final_totals_are_a_tie_and_remaining_charge_does_not_break_it()
    {
        var ctx = GameContext.Create(42, 2);
        var unit = GameRulesV1.MovementScoreUnitFixed;
        var s = Step(At(ctx, GameRulesV1.RoundTicks - 1,
            Player(0, LaneX, U(300), charge: 3, distance: 50L * unit),
            Player(1, U(1400), U(450), charge: 0, distance: 50L * unit)), ctx);
        Assert.Equal(new byte[] { 0, 1 }, s.FinalResult!.Winners);
        Assert.True(s.FinalResult.IsTie);
    }

    // ---------------- Properties ----------------

    private sealed record Case(GameContext Context, GameState Start, IReadOnlyList<(sbyte X, sbyte Y, bool B)?[]> Rows);

    /// <summary>Two to four players clustered on one candle site with random charge, Overlit deadlines, scores, latches and cooldowns.</summary>
    private static Gen<Case> CaseGen(int maxTicks) =>
        from players in Gen.Choose(2, 4)
        from seed in Gen.Choose(0, 10_000)
        from tick in Gen.Choose(1, GameRulesV1.RoundTicks - maxTicks)
        from centre in Gen.Elements(ArenaV1.CandleSites.ToArray())
        from offsets in Gen.ArrayOf(Gen.Choose(-60, 60), 8)
        from charges in Gen.ArrayOf(Gen.Choose(0, 4), 4)
        from deadlines in Gen.ArrayOf(Gen.Choose(1, GameRulesV1.OverlitTicks), 4)
        from points in Gen.ArrayOf(Gen.Choose(0, 300), 4)
        from latches in Gen.ArrayOf(Gen.Elements(new[] { false, true }), 4)
        from cooldowns in Gen.ArrayOf(Gen.Frequency((2, Gen.Constant(0)), (1, Gen.Choose(1, GameRulesV1.ContactCooldownTicks))), 6)
        from length in Gen.Choose(1, maxTicks)
        from rows in Gen.ArrayOf(
            Gen.Frequency(
                (1, Gen.Constant(((sbyte X, sbyte Y, bool B)?)null)),
                (4, from x in Gen.Choose(-1, 1) from y in Gen.Choose(-1, 1) from b in Gen.Elements(new[] { false, true })
                    select ((sbyte X, sbyte Y, bool B)?)((sbyte)x, (sbyte)y, b))),
            4 * length)
        select Build(players, seed, tick, centre, offsets, charges, deadlines, points, latches, cooldowns, length, rows);

    private static Case Build(int n, int seed, int tick, FixedPoint centre, int[] offsets, int[] charges, int[] deadlines,
        int[] points, bool[] latches, int[] cooldowns, int length, (sbyte X, sbyte Y, bool B)?[] rows)
    {
        var ctx = GameContext.Create(seed, n);
        var players = new PlayerState[n];
        for (var i = 0; i < n; i++)
        {
            var x = centre.X + U(offsets[2 * i]);
            var y = centre.Y + U(offsets[2 * i + 1]);
            if (!ArenaV1.CanOccupy(x, y))
            {
                (x, y) = (centre.X, centre.Y);
            }
            var distance = Math.Min((long)points[i] * GameRulesV1.MovementScoreUnitFixed, (long)tick * GameRulesV1.CardinalStepFixed);
            players[i] = new PlayerState((byte)i, (byte)i, x, y, distance)
            {
                Charge = charges[i],
                OverlitDeadlineTick = charges[i] == GameRulesV1.MaxCharge ? tick + deadlines[i] : 0,
                BurstLatch = latches[i],
            };
        }
        var start = At(ctx, tick, players) with
        {
            PairContactReadyTicks = [.. cooldowns.Take(GameState.PairCount(n)).Select(c => c == 0 ? 0L : tick + c)],
        };
        var script = Enumerable.Range(0, length).Select(t => rows.Skip(4 * t).Take(n).ToArray()).ToList();
        return new Case(ctx, start, script);
    }

    private static GameInput[] InputsFor(GameState before, (sbyte X, sbyte Y, bool B)?[] row, int[] seq)
    {
        var list = new List<GameInput>();
        for (byte slot = 0; slot < row.Length; slot++)
        {
            if (row[slot] is { } r)
            {
                list.Add(new GameInput(before.Tick + 1, slot, ++seq[slot], r.X, r.Y, r.B));
            }
        }
        return [.. list];
    }

    /// <summary>
    /// **Validates: Requirements R06.03, R06.04**
    /// Over random clustered worlds and scripts: charge stays in 0..4, Overlit iff charge 4 with a deadline inside the window,
    /// at most one Burst per player per tick, a manual Burst only on a rising edge, Burst owners end at zero, totals never negative,
    /// candles are collected at most once and never return, and every reachable state survives the canonical codec unchanged.
    /// </summary>
    [Fact]
    public void Charge_Burst_and_pickup_invariants_hold_for_every_tick_of_random_contested_play()
    {
        var seen = new Dictionary<GameEventKind, int>();
        Prop.ForAll(CaseGen(40).ToArbitrary(), c =>
        {
            var s = c.Start;
            var seq = new int[4];
            var everCollected = new HashSet<string>();
            foreach (var row in c.Rows)
            {
                var inputs = InputsFor(s, row, seq);
                var next = Step(s, c.Context, inputs);
                for (var slot = 0; slot < next.Players.Length; slot++)
                {
                    var p = next.Players[slot];
                    var bursts = next.Events.Count(e => e.Slot == slot && e.Kind is GameEventKind.BurstManual or GameEventKind.BurstAuto);
                    var manual = next.Events.Any(e => e.Slot == slot && e.Kind == GameEventKind.BurstManual);
                    var edge = inputs.Any(i => i.Slot == slot && i.RequestBurst) && !s.Players[slot].BurstLatch;
                    if (p.Charge is < 0 or > GameRulesV1.MaxCharge
                        || p.IsOverlit != (p.OverlitDeadlineTick > next.Tick && p.OverlitDeadlineTick <= next.Tick + GameRulesV1.OverlitTicks)
                        || bursts > 1 || (bursts == 1 && p.Charge != 0) || (manual && !edge)
                        || p.TotalScore < 0 || p.Bursts - s.Players[slot].Bursts != bursts)
                    {
                        return false;
                    }
                }
                var ids = next.Collected.Select(x => x.Id).ToList();
                if (ids.Distinct().Count() != ids.Count || next.ActiveCandleIds.Any(everCollected.Contains)
                    || next.ActiveCandleIds.Intersect(ids).Any())
                {
                    return false;
                }
                everCollected.UnionWith(ids);
                foreach (var e in next.Events)
                {
                    seen[e.Kind] = seen.GetValueOrDefault(e.Kind) + 1;
                }
                var bytes = GameStateCodec.Instance.EncodeCanonical(next);
                if (!bytes.SequenceEqual(GameStateCodec.Instance.EncodeCanonical(Checkpoint(next))))
                {
                    return false;
                }
                s = next;
            }
            return true;
        }).Check(Config.QuickThrowOnFailure.WithMaxTest(300));
        // The generator must actually exercise every interaction, otherwise the invariants above would hold vacuously.
        foreach (var kind in new[] { GameEventKind.CandleCollected, GameEventKind.OverlitStarted, GameEventKind.ContactDrain,
                     GameEventKind.BurstManual, GameEventKind.BurstAuto, GameEventKind.BurstHit, GameEventKind.OverlitCancelled })
        {
            Assert.True(seen.GetValueOrDefault(kind) > 0, $"No {kind} event was generated.");
        }
    }

    /// <summary>
    /// **Validates: Requirements R06.04**
    /// Same-tick priority is a function of (slot, sequence) only: any submission order, ordinal or input id of the same
    /// inputs yields the identical state hash, including several inputs per slot in one tick.
    /// </summary>
    [Fact]
    public void Same_tick_resolution_is_independent_of_input_arrival_order()
    {
        var gen =
            from c in CaseGen(1)
            from extra in Gen.ArrayOf(
                from x in Gen.Choose(-1, 1) from y in Gen.Choose(-1, 1) from b in Gen.Elements(new[] { false, true }) from slot in Gen.Choose(0, 3)
                select (X: (sbyte)x, Y: (sbyte)y, B: b, Slot: slot), 6)
            from keys in Gen.ArrayOf(Gen.Choose(0, 1_000_000), 6)
            select (c, extra, keys);
        Prop.ForAll(gen.ToArbitrary(), t =>
        {
            var (c, extra, keys) = t;
            var n = c.Start.Players.Length;
            var seq = new int[4];
            var inputs = extra.Where(e => e.Slot < n)
                .Select(e => new GameInput(c.Start.Tick + 1, (byte)e.Slot, ++seq[e.Slot], e.X, e.Y, e.B)).ToList();
            var sorted = Ordered([.. inputs]);
            var shuffled = inputs.Select((g, i) => (g, k: keys[i])).OrderBy(p => p.k).ThenBy(p => p.g.Slot)
                .Select((p, i) => new Marrow.Timeline.OrderedInput<GameInput>($"x{keys[i]}-{i}", inputs.Count - i, p.g)).ToList();
            var a = GhostLapTransition.Instance.Apply(c.Start, c.Start.Tick + 1, sorted, c.Context);
            var b = GhostLapTransition.Instance.Apply(c.Start, c.Start.Tick + 1, shuffled, c.Context);
            return Hash(a) == Hash(b);
        }).Check(Config.QuickThrowOnFailure.WithMaxTest(300));
    }

    /// <summary>
    /// **Validates: Requirements R06.07, R06.11**
    /// From a world with nonzero charge, Overlit, cooldowns and latches, a checkpoint taken at any tick of a contested script
    /// resumes to the same final hash as the uninterrupted run.
    /// </summary>
    [Fact]
    public void Checkpoint_restore_mid_contested_play_matches_the_uninterrupted_run()
    {
        var gen = from c in CaseGen(40) from cut in Gen.Choose(0, 40) select (c, cut);
        Prop.ForAll(gen.ToArbitrary(), t =>
        {
            var (c, cutRaw) = t;
            var cut = Math.Min(cutRaw, c.Rows.Count);
            GameState Run(GameState from, int start, int end, int[] seq)
            {
                var s = from;
                for (var i = start; i < end; i++)
                {
                    s = Step(s, c.Context, InputsFor(s, c.Rows[i], seq));
                }
                return s;
            }
            var whole = Run(c.Start, 0, c.Rows.Count, new int[4]);
            var seq2 = new int[4];
            var partial = Run(c.Start, 0, cut, seq2);
            var resumed = Run(Checkpoint(partial), cut, c.Rows.Count, seq2);
            return Hash(whole) == Hash(resumed);
        }).Check(Config.QuickThrowOnFailure.WithMaxTest(200));
    }
}
