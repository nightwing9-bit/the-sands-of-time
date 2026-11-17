using GhostLap.Rules;
using GhostLap.Simulation;
using Xunit;
using Xunit.Abstractions;
using static GhostLap.Tests.Simulation.GameTestKit;

namespace GhostLap.Tests.Multiplayer;

/// <summary>
/// Spec 10 task 10.0 preflight. Measures the unchanged <c>ghost-v1</c> movement, pickup and contact geometry at the
/// maximum legal speed, and records ghost-v1 behaviour that the multiplayer extension must change. These tests change
/// no production rule; they pin facts the preflight document (docs/architecture/08-ghostlap-multiplayer-preflight.md) cites.
/// </summary>
public sealed class PreflightSpeedSafetyTests(ITestOutputHelper output)
{
    private static readonly (int X, int Y)[] Directions = [(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)];
    private const int H = GameRulesV1.ColliderHalfExtentFixed;

    /// <summary>
    /// **Validates: Requirements R10.11**
    /// d_max from the actual constants: one cardinal step, or a normalized diagonal, per 1/30 s tick. No acceleration exists.
    /// </summary>
    [Fact]
    public void Maximum_per_tick_displacement_is_smaller_than_every_wall_pickup_and_contact_window()
    {
        long dMax = 0;
        var axisMax = 0;
        foreach (var (mx, my) in Directions)
        {
            var r = Movement.Move(U(200), U(450), mx, my); // open floor around socket 0 of the 2P layout
            dMax = Math.Max(dMax, r.DistanceFixed);
            axisMax = Math.Max(axisMax, Math.Max(Math.Abs(r.XFixed - U(200)), Math.Abs(r.YFixed - U(450))));
        }
        var thinnestObstacle = ArenaV1.Obstacles.Min(o => Math.Min(o.X1 - o.X0, o.Y1 - o.Y0));
        var pickupWindow = 2 * (H + GameRulesV1.CandleHalfExtentFixed);
        var contactWindow = 2 * (2 * H);
        var staleTravel = GameRulesV1.InputHoldTicks * axisMax;
        output.WriteLine($"d_max={dMax} fixed ({dMax / (double)GameRulesV1.FixedOne:F3} u/tick), per-axis max={axisMax}, " +
            $"thinnest obstacle={thinnestObstacle}, pickup window={pickupWindow}, contact window={contactWindow}, " +
            $"stale travel after last accepted input={staleTravel}");

        Assert.Equal(GameRulesV1.CardinalStepFixed, axisMax);
        Assert.InRange(dMax, GameRulesV1.CardinalStepFixed, GameRulesV1.CardinalStepFixed + 1);
        Assert.True(axisMax < thinnestObstacle);
        Assert.True(axisMax < pickupWindow);
        Assert.True(2 * axisMax < contactWindow); // two players closing head-on
        Assert.Equal(6 * GameRulesV1.CardinalStepFixed, staleTravel);
    }

    /// <summary>
    /// **Validates: Requirements R10.11**
    /// The per-axis lane resolution is a swept test: the swept box of the X leg and then the Y leg never overlaps an obstacle
    /// and never leaves the world, for every unit-grid start and 200,000 off-grid fixed-point starts, in all eight directions.
    /// </summary>
    [Fact]
    public void No_legal_move_sweeps_through_an_obstacle_or_the_world_edge()
    {
        long moves = 0;
        var failures = 0;
        string? first = null;
        void Check(int x, int y)
        {
            if (!ArenaV1.CanOccupy(x, y))
            {
                return;
            }
            foreach (var (mx, my) in Directions)
            {
                var r = Movement.Move(x, y, mx, my);
                moves++;
                var xLeg = new FixedRect(Math.Min(x, r.XFixed) - H, y - H, Math.Max(x, r.XFixed) + H, y + H);
                var yLeg = new FixedRect(r.XFixed - H, Math.Min(y, r.YFixed) - H, r.XFixed + H, Math.Max(y, r.YFixed) + H);
                var bad = !ArenaV1.CanOccupy(r.XFixed, r.YFixed) || OutsideWorld(xLeg) || OutsideWorld(yLeg);
                foreach (var o in ArenaV1.Obstacles)
                {
                    bad |= xLeg.Overlaps(o) || yLeg.Overlaps(o);
                }
                if (bad)
                {
                    failures++;
                    first ??= $"start=({x},{y}) dir=({mx},{my}) end=({r.XFixed},{r.YFixed})";
                }
            }
        }
        for (var xu = 24; xu <= GameRulesV1.WorldWidthUnits - 24; xu++)
        {
            for (var yu = 24; yu <= GameRulesV1.WorldHeightUnits - 24; yu++)
            {
                Check(U(xu), U(yu));
            }
        }
        var rng = new SplitMix(0x5EED_10_00);
        for (var i = 0; i < 200_000; i++)
        {
            Check(H + rng.Below(GameRulesV1.WorldWidthFixed - 2 * H + 1), H + rng.Below(GameRulesV1.WorldHeightFixed - 2 * H + 1));
        }
        output.WriteLine($"moves checked={moves}, swept overlaps={failures}");
        Assert.True(failures == 0, first);
        Assert.True(moves > 10_000_000);
    }

    /// <summary>
    /// **Validates: Requirements R10.11**
    /// Pickup is an end-of-tick overlap test. Along the actual X-then-Y path, a cardinal move can never pass a candle
    /// unsampled; a diagonal can miss only a corner graze whose per-axis depth is below one diagonal axis step.
    /// </summary>
    [Fact]
    public void Pickup_sampling_misses_only_diagonal_corner_grazes_shallower_than_one_diagonal_axis_step()
    {
        var reach = H + GameRulesV1.CandleHalfExtentFixed; // centre distance per axis below which a collider overlaps a candle
        var band = reach + U(11);
        const int stride = 512; // half a world unit
        long starts = 0, diagonalMisses = 0, cardinalMisses = 0;
        var maxDepth = 0;
        foreach (var site in ArenaV1.CandleSites)
        {
            for (var dx = -band; dx <= band; dx += stride)
            {
                for (var dy = -band; dy <= band; dy += stride)
                {
                    int x = site.X + dx, y = site.Y + dy;
                    if (!ArenaV1.CanOccupy(x, y) || Inside(dx, dy, reach))
                    {
                        continue;
                    }
                    foreach (var (mx, my) in Directions)
                    {
                        starts++;
                        var r = Movement.Move(x, y, mx, my);
                        if (Inside(r.XFixed - site.X, r.YFixed - site.Y, reach))
                        {
                            continue; // collected on this tick
                        }
                        // X leg at the start row, then Y leg at the resolved column (Movement resolves X before Y).
                        var depth = Math.Max(
                            LegDepth(x - site.X, r.XFixed - site.X, y - site.Y, reach),
                            LegDepth(y - site.Y, r.YFixed - site.Y, r.XFixed - site.X, reach));
                        if (depth <= 0)
                        {
                            continue;
                        }
                        if (mx != 0 && my != 0)
                        {
                            diagonalMisses++;
                        }
                        else
                        {
                            cardinalMisses++;
                        }
                        maxDepth = Math.Max(maxDepth, depth);
                    }
                }
            }
        }
        output.WriteLine($"pickup: start/direction cases={starts}, cardinal misses={cardinalMisses}, diagonal corner grazes={diagonalMisses}, " +
            $"max missed depth={maxDepth} fixed ({maxDepth / (double)GameRulesV1.FixedOne:F3} u), diagonal axis step={GameRulesV1.DiagonalStepFixed}");
        Assert.Equal(0, cardinalMisses);
        Assert.True(diagonalMisses > 0, "the sampling gap is real; the preflight records it instead of hiding it");
        Assert.InRange(maxDepth, 1, GameRulesV1.DiagonalStepFixed);
    }

    /// <summary>
    /// **Validates: Requirements R10.11**
    /// Contact is an end-of-tick overlap test of the two colliders. Against a continuous straight-line reference (open floor,
    /// both players moving at full speed in any of 8x8 direction pairs), a pass-through is never missed; only corner grazes
    /// are, and their depth is below the relative per-axis displacement of one tick.
    /// </summary>
    [Fact]
    public void Contact_sampling_misses_only_corner_grazes_shallower_than_one_tick_of_relative_motion()
    {
        var reach = 2 * H;
        var band = reach + U(21);
        const int stride = 512;
        long cases = 0, passThroughMisses = 0, cornerGrazes = 0;
        double maxDepth = 0;
        double maxAllowed = 0;
        foreach (var a in Directions)
        {
            foreach (var b in Directions)
            {
                double rx = AxisStep(b.X, b) - AxisStep(a.X, a), ry = AxisStep(b.Y, b) - AxisStep(a.Y, a); // B relative to A
                for (var dx = -band; dx <= band; dx += stride)
                {
                    for (var dy = -band; dy <= band; dy += stride)
                    {
                        if (Inside(dx, dy, reach) || Inside((int)(dx + rx), (int)(dy + ry), reach))
                        {
                            continue;
                        }
                        cases++;
                        var depth = SegmentDepth(dx, dy, rx, ry, reach);
                        if (depth <= 0)
                        {
                            continue;
                        }
                        var bound = Math.Min(Math.Abs(rx), Math.Abs(ry));
                        if (depth > bound)
                        {
                            passThroughMisses++;
                        }
                        else
                        {
                            cornerGrazes++;
                        }
                        maxDepth = Math.Max(maxDepth, depth);
                        maxAllowed = Math.Max(maxAllowed, bound);
                    }
                }
            }
        }
        output.WriteLine($"contact: cases={cases}, pass-through misses={passThroughMisses}, corner grazes={cornerGrazes}, " +
            $"max missed depth={maxDepth:F0} fixed ({maxDepth / GameRulesV1.FixedOne:F3} u)");
        Assert.Equal(0, passThroughMisses);
        Assert.True(cornerGrazes > 0);
        Assert.True(maxDepth <= 2 * GameRulesV1.DiagonalStepFixed);
    }

    /// <summary>
    /// **Validates: Requirements R10.07**
    /// Under delay or loss, a Burst press that is rejected as late does not fire; the next accepted input that still carries
    /// the pressed state creates exactly one rising edge, and repeated pressed-state inputs never fire again.
    /// </summary>
    [Fact]
    public void A_late_rejected_Burst_press_is_recovered_once_by_the_next_pressed_input_and_never_repeats()
    {
        var ctx = GameContext.Create(42, 2);
        var s = At(ctx, 200, Player(0, U(200), U(450), charge: 4, deadline: 400), Player(1, U(1400), U(450)));
        var late = InputAdmission.Admit(s, [], new SubmittedInput(0, 0, s.Tick + 1 - GameRulesV1.InputMaxLateTicks - 1, 0, 0, true));
        Assert.Equal(InputRejection.LateTick, late.Error);
        var bursts = 0;
        for (var seq = 1; seq <= 12; seq++)
        {
            var admitted = InputAdmission.Admit(s, [], new SubmittedInput(0, seq, s.Tick + 1, 0, 0, true));
            Assert.True(admitted.IsSuccess);
            s = Step(s, ctx, admitted.Value);
            bursts += EventsOf(s, GameEventKind.BurstManual).Length + EventsOf(s, GameEventKind.BurstAuto).Length;
        }
        Assert.Equal(1, bursts);
        Assert.Equal(1, s.Players[0].Bursts);
    }

    /// <summary>
    /// **Validates: Requirements R10.06**
    /// Evidence for the compatibility matrix: ghost-v1 has no inactive-slot state. A slot that only receives the
    /// disconnect neutral input still auto-Bursts and hits rivals, and still collects a candle it stands on. Multiplayer
    /// forfeit therefore needs a versioned state/reducer extension, not a neutral input.
    /// </summary>
    [Fact]
    public void Ghost_v1_neutralized_slot_still_auto_Bursts_and_collects()
    {
        var ctx = GameContext.Create(42, 2);
        var (near, far) = ClearPair(U(120));
        var s = At(ctx, 300, Player(0, near.X, near.Y), Player(1, near.X + U(120), near.Y, charge: 4, deadline: 301));
        var after = Step(s, ctx, InputAdmission.Neutralize(s, [], 1));
        Assert.Single(EventsOf(after, GameEventKind.BurstAuto));
        Assert.Equal((byte)1, EventsOf(after, GameEventKind.BurstAuto)[0].Slot);
        Assert.Single(EventsOf(after, GameEventKind.BurstHit));

        var candle = CandleActiveAt(ctx, 501);
        var c = At(ctx, 500, Player(0, far.X, far.Y), Player(1, candle.Site.X, candle.Site.Y));
        var collected = Step(c, ctx, InputAdmission.Neutralize(c, [], 1));
        Assert.Contains(EventsOf(collected, GameEventKind.CandleCollected), e => e.Slot == 1 && e.CandleId == candle.Id);
    }

    private static int AxisStep(int component, (int X, int Y) dir) =>
        component * (dir.X != 0 && dir.Y != 0 ? GameRulesV1.DiagonalStepFixed : GameRulesV1.CardinalStepFixed);

    private static bool Inside(int dx, int dy, int reach) => Math.Abs(dx) < reach && Math.Abs(dy) < reach;

    private static bool OutsideWorld(FixedRect r) =>
        r.X0 < 0 || r.Y0 < 0 || r.X1 > GameRulesV1.WorldWidthFixed || r.Y1 > GameRulesV1.WorldHeightFixed;

    /// <summary>
    /// Depth of a straight axis leg into the open square |p| &lt; reach: the leg runs along one axis from
    /// <paramref name="from"/> to <paramref name="to"/> at the constant cross coordinate <paramref name="cross"/>.
    /// Depth is min(reach - |along|, reach - |cross|) at the leg's deepest point; 0 or less means no intrusion.
    /// </summary>
    private static int LegDepth(int from, int to, int cross, int reach)
    {
        var crossDepth = reach - Math.Abs(cross);
        var nearest = Math.Min(from, to) <= 0 && Math.Max(from, to) >= 0 ? 0 : Math.Min(Math.Abs(from), Math.Abs(to));
        return Math.Min(crossDepth, reach - nearest);
    }

    /// <summary>Maximum over t in [0,1] of min(reach - |x(t)|, reach - |y(t)|) for p(t) = (x0, y0) + t (rx, ry); concave, so breakpoints suffice.</summary>
    private static double SegmentDepth(double x0, double y0, double rx, double ry, double reach)
    {
        double F(double t) => Math.Min(reach - Math.Abs(x0 + t * rx), reach - Math.Abs(y0 + t * ry));
        var best = Math.Max(F(0), F(1));
        foreach (var (num, den) in new[] { (-x0, rx), (-y0, ry), (y0 - x0, rx - ry), (-(x0 + y0), rx + ry) })
        {
            if (den != 0 && num / den is var t and > 0 and < 1)
            {
                best = Math.Max(best, F(t));
            }
        }
        return best;
    }

    /// <summary>Two clear, occupiable spots <paramref name="gap"/> apart on one row, touching no candle site.</summary>
    private static (FixedPoint Near, FixedPoint Far) ClearPair(int gap)
    {
        FixedPoint? near = null, far = null;
        for (var yu = 40; yu < 860 && (near is null || far is null); yu += 10)
        {
            for (var xu = 40; xu < 1400; xu += 10)
            {
                if (near is null && ClearSpot(U(xu), U(yu)) && ClearSpot(U(xu) + gap, U(yu)))
                {
                    near = new FixedPoint(U(xu), U(yu));
                }
                else if (near is not null && far is null && ClearSpot(U(xu), U(yu)))
                {
                    far = new FixedPoint(U(xu), U(yu));
                }
            }
        }
        return (near ?? throw new InvalidOperationException("no clear pair"), far ?? throw new InvalidOperationException("no far spot"));
    }

    private sealed class SplitMix(ulong state)
    {
        public int Below(int bound)
        {
            unchecked
            {
                var z = state += 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return (int)((z ^ (z >> 31)) % (ulong)bound);
            }
        }
    }
}
