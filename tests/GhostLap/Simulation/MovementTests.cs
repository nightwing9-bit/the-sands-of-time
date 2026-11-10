using FsCheck;
using FsCheck.Fluent;
using GhostLap.Rules;
using GhostLap.Simulation;
using Xunit;

namespace GhostLap.Tests.Simulation;

public sealed class MovementTests
{
    private static int U(int units) => GameRulesV1.ToFixed(units);

    [Fact]
    public void Cardinal_move_is_ten_units_and_distance_equals_displacement()
    {
        var r = Movement.Move(U(200), U(450), 1, 0);
        Assert.Equal(U(210), r.XFixed);
        Assert.Equal(U(450), r.YFixed);
        Assert.Equal(U(10), r.DistanceFixed);
    }

    [Fact]
    public void Diagonal_move_is_normalized_to_about_the_cardinal_distance()
    {
        var r = Movement.Move(U(200), U(450), 1, -1);
        Assert.Equal(U(200) + GameRulesV1.DiagonalStepFixed, r.XFixed);
        Assert.Equal(U(450) - GameRulesV1.DiagonalStepFixed, r.YFixed);
        Assert.InRange(r.DistanceFixed, GameRulesV1.CardinalStepFixed - 2, GameRulesV1.CardinalStepFixed + 2);
    }

    [Fact]
    public void Neutral_input_does_not_move_or_earn_distance()
    {
        var r = Movement.Move(U(200), U(450), 0, 0);
        Assert.Equal(new Movement.Result(U(200), U(450), 0), r);
    }

    [Fact]
    public void Boundary_stops_flush_and_earns_no_distance_while_blocked()
    {
        var h = GameRulesV1.ColliderHalfExtentFixed;
        var r = Movement.Move(h + 3 * GameRulesV1.FixedOne, U(450), -1, 0);
        Assert.Equal(h, r.XFixed);
        Assert.Equal(3 * GameRulesV1.FixedOne, r.DistanceFixed);
        var again = Movement.Move(r.XFixed, r.YFixed, -1, 0);
        Assert.Equal(0, again.DistanceFixed);
    }

    [Fact]
    public void Moving_into_an_obstacle_stops_flush_against_its_face()
    {
        // Left post occupies x 300..340, y 380..520. A collider at (250, 450) moving right stops with its right edge at x=300.
        var r = Movement.Move(U(272), U(450), 1, 0);
        Assert.Equal(U(300) - GameRulesV1.ColliderHalfExtentFixed, r.XFixed);
        Assert.False(ArenaV1.ColliderAt(r.XFixed, r.YFixed).Overlaps(ArenaV1.Obstacles[4]));
    }

    [Fact]
    public void Diagonal_into_a_wall_slides_along_it()
    {
        // Same post: moving right+down while flush against its left face keeps X fixed and still advances Y.
        var x = U(300) - GameRulesV1.ColliderHalfExtentFixed;
        var r = Movement.Move(x, U(450), 1, 1);
        Assert.Equal(x, r.XFixed);
        Assert.Equal(U(450) + GameRulesV1.DiagonalStepFixed, r.YFixed);
        Assert.Equal(GameRulesV1.DiagonalStepFixed, r.DistanceFixed);
    }

    [Fact]
    public void Diagonal_into_a_corner_stops_and_resolves_x_before_y()
    {
        // Top-left corner of the world: blocked on both axes.
        var h = GameRulesV1.ColliderHalfExtentFixed;
        var r = Movement.Move(h, h, -1, -1);
        Assert.Equal(new Movement.Result(h, h, 0), r);
    }

    [Fact]
    public void Move_rejects_out_of_range_vectors()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Movement.Move(U(200), U(450), 2, 0));
    }

    /// <summary>Property: from any occupiable start, any input sequence keeps the collider legal and distance exact to displacement.</summary>
    [Fact]
    public void Collider_never_overlaps_an_obstacle_or_leaves_the_world()
    {
        var start = Gen.Elements(ArenaV1.SpawnSockets(4).Concat(ArenaV1.CandleSites).ToArray());
        var moves = Gen.Choose(-1, 1).Zip(Gen.Choose(-1, 1)).ListOf().Select(l => l.Take(400).ToList());
        Prop.ForAll(start.Zip(moves).ToArbitrary(), t =>
        {
            var (p, list) = t;
            int x = p.X, y = p.Y;
            foreach (var (mx, my) in list)
            {
                var r = Movement.Move(x, y, mx, my);
                var legal = ArenaV1.CanOccupy(r.XFixed, r.YFixed)
                    && r.DistanceFixed <= GameRulesV1.CardinalStepFixed + 1
                    && (r.XFixed != x || r.YFixed != y || r.DistanceFixed == 0);
                if (!legal)
                {
                    return false;
                }
                (x, y) = (r.XFixed, r.YFixed);
            }
            return true;
        }).Check(Config.QuickThrowOnFailure.WithMaxTest(200));
    }

    [Fact]
    public void Integer_square_root_is_the_exact_floor()
    {
        Prop.ForAll(Gen.Choose(0, int.MaxValue).Select(i => (long)i * 4 + i % 3).ToArbitrary(), v =>
        {
            var s = GameRulesV1.IntegerSqrt(v);
            return s * s <= v && (s + 1) * (s + 1) > v;
        }).Check(Config.QuickThrowOnFailure.WithMaxTest(500));
        Assert.Equal(0, GameRulesV1.IntegerSqrt(0));
        Assert.Equal(1, GameRulesV1.IntegerSqrt(1));
        Assert.Equal(3, GameRulesV1.IntegerSqrt(15));
        Assert.Equal(4, GameRulesV1.IntegerSqrt(16));
    }
}
