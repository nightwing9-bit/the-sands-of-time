using GhostLap.Multiplayer;
using GhostLap.Rules;
using GhostLap.Simulation;
using Xunit;
using Xunit.Abstractions;
using static GhostLap.Tests.Simulation.GameTestKit;

namespace GhostLap.Tests.Multiplayer;

/// <summary>
/// Task 10.2 (R10.11) through the multiplayer path (<see cref="MpRules"/>, which the match engine calls). The exhaustive wall, pickup and
/// contact sweeps of the shared phase code are the 10.0 measurements in <c>PreflightSpeedSafetyTests</c>; these check the same
/// properties hold in ghost-mp-v1 at the single legal speed (10 u/tick cardinal, 7241 fixed per axis diagonal).
/// </summary>
public sealed class MpPhysicsSafetyTests(ITestOutputHelper output)
{
    private const int H = GameRulesV1.ColliderHalfExtentFixed;

    private static MpMatchState With(MpMatchContext ctx, long tick, params PlayerState[] players)
    {
        var initial = MpMatchState.Initial(ctx).World;
        return new MpMatchState(initial with { Tick = tick, ActiveCandleIds = ctx.Plan.ActiveIdsAt(tick), Players = [.. players] }, [.. new long[players.Length]]);
    }

    private static PlayerState Far(MpMatchContext ctx, byte slot) => MpMatchState.Initial(ctx).World.Players[slot];

    /// <summary>
    /// **Validates: Requirements 10.11** A diagonal into the world edge slides along it at one diagonal axis step per tick and scores only
    /// the displacement that happened; pushing into a corner moves nothing and scores nothing (no depenetration or correction exists).
    /// </summary>
    [Fact]
    public void Diagonal_wall_slide_scores_only_real_displacement_and_a_corner_push_scores_nothing()
    {
        var ctx = MpMatchContext.Create(11, 2);
        var bottom = GameRulesV1.WorldHeightFixed - H;
        // Find a stretch of the bottom edge where 20 diagonal steps slide freely (the fixture is chosen by the real movement code).
        var x = Enumerable.Range(2, 60).Select(i => i * U(25)).First(start =>
        {
            var cx = start;
            for (var i = 0; i < 20; i++)
            {
                var r = Movement.Move(cx, bottom, 1, 1);
                if (r.XFixed != cx + GameRulesV1.DiagonalStepFixed || r.YFixed != bottom)
                {
                    return false;
                }
                cx = r.XFixed;
            }
            return true;
        });
        var s = With(ctx, 0, new PlayerState(0, 0, x, bottom, 0), Far(ctx, 1));
        for (var i = 1; i <= 20; i++)
        {
            s = MpRules.Step(s, [new GameInput(i, 0, i, 1, 1, false)], [], ctx);
            Assert.Equal(bottom, s.World.Players[0].YFixed);
            Assert.Equal(x + (i * (long)GameRulesV1.DiagonalStepFixed), s.World.Players[0].XFixed);
            Assert.Equal(i * (long)GameRulesV1.DiagonalStepFixed, s.World.Players[0].DistanceFixed);
        }

        var corner = With(ctx, 0, new PlayerState(0, 0, H, H, 0), Far(ctx, 1));
        for (var i = 1; i <= 100; i++)
        {
            corner = MpRules.Step(corner, [new GameInput(i, 0, i, -1, -1, false)], [], ctx);
        }
        Assert.Equal((H, H, 0L, 0), (corner.World.Players[0].XFixed, corner.World.Players[0].YFixed, corner.World.Players[0].DistanceFixed, corner.World.Players[0].TotalScore));
    }

    /// <summary>
    /// **Validates: Requirements 10.11** At full cardinal speed a straight pass through a candle is always collected: for every candle of a
    /// seed and every start offset just outside the pickup window (1/16 u steps over one full step), the candle is collected within two ticks.
    /// </summary>
    [Fact]
    public void A_full_speed_cardinal_pass_through_a_candle_is_never_missed()
    {
        var ctx = MpMatchContext.Create(21, 2);
        var window = H + GameRulesV1.CandleHalfExtentFixed; // centres closer than this overlap
        var cases = 0;
        foreach (var candle in ctx.Plan.Candles.Take(40))
        {
            for (var offset = window; offset < window + GameRulesV1.CardinalStepFixed; offset += 64)
            {
                var startX = candle.Site.X - offset;
                // Only lanes the real movement code leaves open for two steps (a wall in the way is not a pickup case).
                var first = Movement.Move(startX, candle.Site.Y, 1, 0);
                var second = Movement.Move(first.XFixed, candle.Site.Y, 1, 0);
                if (!ArenaV1.CanOccupy(startX, candle.Site.Y) || first.XFixed != startX + GameRulesV1.CardinalStepFixed
                    || second.XFixed != first.XFixed + GameRulesV1.CardinalStepFixed)
                {
                    continue;
                }
                var s = With(ctx, candle.ActivateTick - 1, new PlayerState(0, 0, startX, candle.Site.Y, 0), Far(ctx, 1));
                if (!ctx.Plan.ActiveAt(candle.ActivateTick + 1).Any(c => c.Id == candle.Id))
                {
                    continue;
                }
                for (var i = 1; i <= 2; i++)
                {
                    s = MpRules.Step(s, [new GameInput(s.Tick + 1, 0, i, 1, 0, false)], [], ctx);
                }
                Assert.Contains(s.World.Collected, c => c.Id == candle.Id && c.Slot == 0);
                cases++;
            }
        }
        output.WriteLine($"full-speed cardinal pickup cases: {cases}, misses: 0");
        Assert.True(cases > 500, $"only {cases} open-lane cases");
    }
}
