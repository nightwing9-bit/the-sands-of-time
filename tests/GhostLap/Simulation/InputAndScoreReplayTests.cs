using FsCheck;
using FsCheck.Fluent;
using GhostLap.Rules;
using GhostLap.Simulation;
using Xunit;
using static GhostLap.Tests.Simulation.GameTestKit;

namespace GhostLap.Tests.Simulation;

/// <summary>Effective input (hold, neutral, Burst edge), admission window and fixed-point score replay (task 6.2).</summary>
public sealed class InputAndScoreReplayTests
{
    // Solo spawn (200, 450) has open floor to its right up to the left post at x = 300.
    private static GameContext Solo() => GameContext.Create(42, 1);

    [Fact]
    public void A_vector_is_held_for_six_ticks_without_input_and_then_becomes_neutral()
    {
        var ctx = Solo();
        var s0 = GameState.Initial(ctx);
        var s = Step(s0, ctx, In(s0, 0, 1, 0, 1)); // tick 1, moving down
        var ys = new List<int> { s.Players[0].YFixed };
        for (var i = 0; i < 8; i++)
        {
            s = Step(s, ctx);
            ys.Add(s.Players[0].YFixed);
        }
        var step = GameRulesV1.CardinalStepFixed;
        var start = s0.Players[0].YFixed;
        // Ticks 1..7 move (the input tick plus six held ticks); ticks 8 and 9 are neutral.
        Assert.Equal(Enumerable.Range(1, 7).Select(k => start + k * step).Concat(new[] { start + 7 * step, start + 7 * step }), ys);
        Assert.Equal((0, 0, 0L), (s.Players[0].HeldMoveX, s.Players[0].HeldMoveY, s.Players[0].HoldUntilTick));
    }

    [Fact]
    public void An_explicit_neutral_and_a_disconnect_neutralize_immediately_and_release_the_Burst_latch()
    {
        var ctx = Solo();
        var s0 = GameState.Initial(ctx);
        var s1 = Step(s0, ctx, In(s0, 0, 1, 0, 1, burst: true));
        var s2 = Step(s1, ctx, In(s1, 0, 2, 0, 0));
        Assert.Equal(s1.Players[0].YFixed, s2.Players[0].YFixed);

        var s3 = Step(s2, ctx, In(s2, 0, 3, 0, 1, burst: true));
        var drop = InputAdmission.Neutralize(s3, [], 0);
        Assert.Equal(GameInput.Neutral(s3.Tick + 1, 0, 4), drop);
        var s4 = Step(s3, ctx, drop);
        Assert.Equal(s3.Players[0].YFixed, s4.Players[0].YFixed);
        Assert.False(s4.Players[0].BurstLatch);
        Assert.Equal(4, s4.Players[0].LastSequence);
    }

    /// <summary>
    /// **Validates: Requirements R06.11**
    /// For any pattern of present and missing inputs, the effective vector each tick equals the reference policy
    /// (latest accepted vector if at most six ticks old, else neutral), and the position is exactly that vector's move.
    /// </summary>
    [Fact]
    public void Effective_movement_matches_the_hold_policy_for_any_missing_input_pattern()
    {
        var row = Gen.Frequency(
            (3, Gen.Constant(((sbyte X, sbyte Y)?)null)),
            (1, from x in Gen.Choose(-1, 1) from y in Gen.Choose(-1, 1) select ((sbyte X, sbyte Y)?)((sbyte)x, (sbyte)y)));
        Prop.ForAll(Gen.ArrayOf(row, 120).ToArbitrary(), rows =>
        {
            var ctx = Solo();
            var s = GameState.Initial(ctx);
            (sbyte X, sbyte Y) last = (0, 0);
            long lastTick = -100;
            var seq = 0;
            foreach (var r in rows)
            {
                var tick = s.Tick + 1;
                (sbyte X, sbyte Y) expected;
                GameInput[] inputs;
                if (r is { } v)
                {
                    (last, lastTick, expected) = (v, tick, v);
                    inputs = [In(s, 0, ++seq, v.X, v.Y)];
                }
                else
                {
                    expected = tick - lastTick <= GameRulesV1.InputHoldTicks ? last : ((sbyte)0, (sbyte)0);
                    inputs = [];
                }
                var before = s.Players[0];
                s = Step(s, ctx, inputs);
                var p = s.Players[0];
                var move = Movement.Move(before.XFixed, before.YFixed, expected.X, expected.Y);
                if ((p.HeldMoveX, p.HeldMoveY) != expected || (p.XFixed, p.YFixed) != (move.XFixed, move.YFixed)
                    || p.DistanceFixed - before.DistanceFixed != move.DistanceFixed)
                {
                    return false;
                }
            }
            return true;
        }).Check(Config.QuickThrowOnFailure.WithMaxTest(200));
    }

    [Fact]
    public void Admission_rejects_stale_late_future_spoofed_and_post_round_inputs_with_typed_failures()
    {
        var ctx = GameContext.Create(42, 2);
        var s0 = GameState.Initial(ctx);
        var s = Step(s0, ctx, In(s0, 0, 5, 1, 0)); // tick 1, slot 0 last sequence 5
        InputRejection? Reject(SubmittedInput i, params GameInput[] pending) =>
            InputAdmission.Admit(s, pending, i).TryGetError(out var e) ? e : null;

        var ok = InputAdmission.Admit(s, [], new SubmittedInput(0, 6, 2, 1, 1, true));
        Assert.True(ok.IsSuccess);
        Assert.Equal(new GameInput(2, 0, 6, 1, 1, true), ok.Value);

        Assert.Equal(InputRejection.StaleSequence, Reject(new SubmittedInput(0, 5, 2, 1, 0, false)));
        Assert.Equal(InputRejection.StaleSequence, Reject(new SubmittedInput(0, 6, 2, 1, 0, false), new GameInput(2, 0, 6, 0, 0, false)));
        Assert.Null(Reject(new SubmittedInput(1, 0, 2, 1, 0, false), new GameInput(2, 0, 6, 0, 0, false))); // other slot unaffected
        Assert.Equal(InputRejection.LateTick, Reject(new SubmittedInput(0, 6, 2 - GameRulesV1.InputMaxLateTicks - 1, 1, 0, false)));
        Assert.Null(Reject(new SubmittedInput(0, 6, 2 - GameRulesV1.InputMaxLateTicks, 1, 0, false)));
        Assert.Equal(InputRejection.FutureTick, Reject(new SubmittedInput(0, 6, 2 + GameRulesV1.InputMaxLeadTicks + 1, 1, 0, false)));
        Assert.Equal(InputRejection.InvalidVector, Reject(new SubmittedInput(0, 6, 2, 3, 0, false)));
        Assert.Equal(InputRejection.UnknownSlot, Reject(new SubmittedInput(2, 6, 2, 1, 0, false)));
        var over = s with { Tick = GameRulesV1.RoundTicks };
        Assert.Equal(InputRejection.RoundOver,
            InputAdmission.Admit(over, [], new SubmittedInput(0, 6, GameRulesV1.RoundTicks, 1, 0, false)).Error);

        // The reducer itself also refuses a stale sequence instead of applying it.
        Assert.Throws<GhostLapTransitionException>(() => Step(s, ctx, In(s, 0, 5, 1, 0)));
        Assert.Throws<GhostLapTransitionException>(() => Step(s, ctx, In(s, 0, 7, 1, 0), In(s, 0, 7, -1, 0)));
    }

    /// <summary>
    /// **Validates: Requirements R06.02, R06.11**
    /// Input spam cannot buy extra movement: however many inputs a slot sends in one tick, it moves exactly one step of the
    /// highest-sequence vector and earns only that post-collision distance.
    /// </summary>
    [Fact]
    public void Input_spam_moves_one_step_of_the_latest_vector_per_tick()
    {
        Prop.ForAll(Gen.ArrayOf(Gen.Choose(0, 5).SelectMany(k => Gen.ArrayOf(Gen.Choose(-1, 1).Zip(Gen.Choose(-1, 1)), k)), 60).ToArbitrary(), ticks =>
        {
            var ctx = Solo();
            var s = GameState.Initial(ctx);
            var seq = 0;
            var held = (X: 0, Y: 0);
            long heldUntil = 0;
            foreach (var spam in ticks)
            {
                var inputs = spam.Select(v => In(s, 0, ++seq, v.Item1, v.Item2)).ToArray();
                if (inputs.Length > 0)
                {
                    held = (inputs[^1].MoveX, inputs[^1].MoveY);
                    heldUntil = s.Tick + 1 + GameRulesV1.InputHoldTicks;
                }
                else if (s.Tick + 1 > heldUntil)
                {
                    held = (0, 0);
                }
                var before = s.Players[0];
                s = Step(s, ctx, inputs);
                var move = Movement.Move(before.XFixed, before.YFixed, held.X, held.Y);
                if ((s.Players[0].XFixed, s.Players[0].YFixed) != (move.XFixed, move.YFixed)
                    || s.Players[0].DistanceFixed - before.DistanceFixed > GameRulesV1.CardinalStepFixed + 1)
                {
                    return false;
                }
            }
            return true;
        }).Check(Config.QuickThrowOnFailure.WithMaxTest(200));
    }

    [Fact]
    public void Movement_points_come_from_the_full_fixed_point_distance_at_the_1023_of_1024_boundary()
    {
        var ctx = Solo();
        var unit = GameRulesV1.MovementScoreUnitFixed;
        var s0 = GameState.Initial(ctx);
        // One fixed unit (1/1024 of a world unit) short of the 5th point, i.e. 1023/1024 into the last world unit; a cardinal step crosses it.
        var p = s0.Players[0] with { DistanceFixed = 5L * unit - 1 };
        var start = s0 with { Tick = 100, Players = [p] };
        Assert.Equal(4, start.Players[0].MovementPoints);
        var uninterrupted = Step(start, ctx, In(start, 0, 1, 1, 0));
        var restored = Step(Checkpoint(start), ctx, In(start, 0, 1, 1, 0));
        Assert.Equal(5, uninterrupted.Players[0].MovementPoints);
        Assert.Equal(5L * unit - 1 + GameRulesV1.CardinalStepFixed, uninterrupted.Players[0].DistanceFixed);
        Assert.Equal(Hash(uninterrupted), Hash(restored));
    }

    /// <summary>
    /// **Validates: Requirements R06.07, R06.11**
    /// Around every score-unit boundary (within one world unit either side of a step landing exactly on it), the point is
    /// earned on the same tick whether or not the run was checkpointed just before, and equals floor(distance / unit).
    /// </summary>
    [Fact]
    public void Checkpoint_just_before_a_score_unit_boundary_earns_the_next_point_on_the_same_tick()
    {
        var gen = from k in Gen.Choose(1, 160) from delta in Gen.Choose(-GameRulesV1.FixedOne, GameRulesV1.FixedOne)
                  from dir in Gen.Elements(new[] { (1, 0), (0, 1), (1, 1), (0, -1) })
                  select (k, delta, dir);
        Prop.ForAll(gen.ToArbitrary(), t =>
        {
            var (k, delta, dir) = t;
            var ctx = Solo();
            var unit = GameRulesV1.MovementScoreUnitFixed;
            var s0 = GameState.Initial(ctx);
            var d0 = (long)k * unit - GameRulesV1.CardinalStepFixed + delta;
            var start = s0 with { Tick = 1000, Players = [s0.Players[0] with { DistanceFixed = d0 }] };
            var input = In(start, 0, 1, dir.Item1, dir.Item2);
            var a = Step(start, ctx, input);
            var b = Step(Checkpoint(start), ctx, input);
            return Hash(a) == Hash(b)
                && a.Players[0].MovementPoints == (int)(a.Players[0].DistanceFixed / unit)
                && a.Players[0].DistanceFixed == d0 + Movement.Move(start.Players[0].XFixed, start.Players[0].YFixed, dir.Item1, dir.Item2).DistanceFixed;
        }).Check(Config.QuickThrowOnFailure.WithMaxTest(300));
    }

    /// <summary>
    /// **Validates: Requirements R06.11**
    /// A walk with diagonal, sliding and held movement, checkpointed after every single tick, produces the same
    /// movement-point curve and final hash as the uninterrupted walk.
    /// </summary>
    [Fact]
    public void Checkpointing_every_tick_reproduces_the_uninterrupted_score_curve()
    {
        var row = Gen.Frequency(
            (2, Gen.Constant(((sbyte X, sbyte Y)?)null)),
            (1, from x in Gen.Choose(-1, 1) from y in Gen.Choose(-1, 1) select ((sbyte X, sbyte Y)?)((sbyte)x, (sbyte)y)));
        Prop.ForAll(Gen.ArrayOf(row, 300).ToArbitrary(), rows =>
        {
            var ctx = Solo();
            GameState a = GameState.Initial(ctx), b = a;
            var seq = 0;
            foreach (var r in rows)
            {
                GameInput[] inputs = r is { } v ? [In(a, 0, ++seq, v.X, v.Y)] : [];
                a = Step(a, ctx, inputs);
                b = Step(Checkpoint(b), ctx, inputs);
                if (a.Players[0].MovementPoints != b.Players[0].MovementPoints)
                {
                    return false;
                }
            }
            return Hash(a) == Hash(b);
        }).Check(Config.QuickThrowOnFailure.WithMaxTest(50));
    }
}
