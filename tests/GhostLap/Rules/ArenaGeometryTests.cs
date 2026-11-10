using FsCheck;
using FsCheck.Fluent;
using GhostLap.Rules;
using Xunit;

namespace GhostLap.Tests.Rules;

public sealed class ArenaGeometryTests
{
    private const int CellUnits = 10;

    [Fact]
    public void Layout_has_nine_obstacles_all_inside_the_world_and_point_symmetric()
    {
        Assert.Equal(9, ArenaV1.Obstacles.Length);
        var world = new FixedRect(0, 0, GameRulesV1.WorldWidthFixed, GameRulesV1.WorldHeightFixed);
        foreach (var o in ArenaV1.Obstacles)
        {
            Assert.True(o.X0 >= world.X0 && o.Y0 >= world.Y0 && o.X1 <= world.X1 && o.Y1 <= world.Y1 && o.X0 < o.X1 && o.Y0 < o.Y1);
            var mirror = new FixedRect(
                GameRulesV1.WorldWidthFixed - o.X1, GameRulesV1.WorldHeightFixed - o.Y1,
                GameRulesV1.WorldWidthFixed - o.X0, GameRulesV1.WorldHeightFixed - o.Y0);
            Assert.Contains(mirror, ArenaV1.Obstacles);
        }
        for (var i = 0; i < ArenaV1.Obstacles.Length; i++)
        {
            for (var j = i + 1; j < ArenaV1.Obstacles.Length; j++)
            {
                Assert.False(ArenaV1.Obstacles[i].Overlaps(ArenaV1.Obstacles[j]));
            }
        }
    }

    [Fact]
    public void Candle_sites_are_twenty_four_distinct_occupiable_points()
    {
        Assert.Equal(24, ArenaV1.CandleSites.Length);
        Assert.Equal(24, ArenaV1.CandleSites.Distinct().Count());
        Assert.All(ArenaV1.CandleSites, s => Assert.True(ArenaV1.CanOccupy(s.X, s.Y), $"site {s} is blocked"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Spawn_sockets_are_occupiable_distinct_and_not_on_a_candle_site(int players)
    {
        var sockets = ArenaV1.SpawnSockets(players);
        Assert.Equal(players, sockets.Length);
        Assert.Equal(players, sockets.Distinct().Count());
        foreach (var s in sockets)
        {
            Assert.True(ArenaV1.CanOccupy(s.X, s.Y), $"socket {s} is blocked");
            Assert.DoesNotContain(s, ArenaV1.CandleSites);
        }
    }

    [Fact]
    public void Two_player_sockets_are_opposite_and_four_player_sockets_are_the_corners()
    {
        var two = ArenaV1.SpawnSockets(2);
        Assert.Equal(GameRulesV1.WorldWidthFixed, two[0].X + two[1].X);
        Assert.Equal(GameRulesV1.WorldHeightFixed, two[0].Y + two[1].Y);
        var four = ArenaV1.SpawnSockets(4);
        Assert.All(four, s => Assert.True(
            (s.X < GameRulesV1.WorldWidthFixed / 4 || s.X > GameRulesV1.WorldWidthFixed * 3 / 4)
            && (s.Y < GameRulesV1.WorldHeightFixed / 4 || s.Y > GameRulesV1.WorldHeightFixed * 3 / 4)));
    }

    [Fact]
    public void Three_player_triangle_has_near_equal_side_lengths()
    {
        var s = ArenaV1.SpawnSockets(3);
        var sides = new[] { Dist(s[0], s[1]), Dist(s[1], s[2]), Dist(s[0], s[2]) };
        Assert.True(sides.Max() - sides.Min() <= 0.15 * sides.Max(), $"sides {string.Join(",", sides)}");
    }

    [Fact]
    public void Unsupported_player_counts_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ArenaV1.SpawnSockets(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ArenaV1.SpawnSockets(5));
    }

    /// <summary>Property: for 2, 3 and 4 players and any seed, every candle site is reachable from every socket.</summary>
    [Fact]
    public void Every_scheduled_candle_is_reachable_from_every_socket_for_2_3_4_players()
    {
        Prop.ForAll(Gen.Choose(2, 4).Zip(Gen.Choose(int.MinValue, int.MaxValue).Select(i => (long)i * 7919)).ToArbitrary(), t =>
        {
            var (players, seed) = t;
            var plan = CandlePlan.Create(seed);
            foreach (var socket in ArenaV1.SpawnSockets(players))
            {
                var dist = PathDistances(socket);
                if (plan.Candles.Any(c => !dist.ContainsKey(Cell(c.Site))))
                {
                    return false;
                }
            }
            return true;
        }).Check(Config.QuickThrowOnFailure.WithMaxTest(40));
    }

    /// <summary>
    /// Opportunity fairness: over a whole round, the mean walking distance from a socket to the scheduled candles
    /// differs by at most <see cref="FairnessRatio"/> between the best and worst slot, for 2, 3 and 4 players.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Mean_candle_distance_is_balanced_between_slots(int players)
    {
        Prop.ForAll(Gen.Choose(0, 100_000).Select(i => (long)i).ToArbitrary(), seed =>
        {
            var plan = CandlePlan.Create(seed);
            var means = ArenaV1.SpawnSockets(players).Select(socket =>
            {
                var dist = PathDistances(socket);
                return plan.Candles.Average(c => (double)dist[Cell(c.Site)]);
            }).ToArray();
            return means.Max() / means.Min() <= FairnessRatio;
        }).Check(Config.QuickThrowOnFailure.WithMaxTest(40));
    }

    internal const double FairnessRatio = 1.4; // measured worst over 101 seeds (step 997): 1.154 (2P), 1.307 (3P), 1.158 (4P)

    private static double Dist(FixedPoint a, FixedPoint b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy) / GameRulesV1.FixedOne;
    }

    private static (int X, int Y) Cell(FixedPoint p) =>
        (p.X / GameRulesV1.FixedOne / CellUnits, p.Y / GameRulesV1.FixedOne / CellUnits);

    /// <summary>Breadth-first 4-neighbour walking distance in world units over a 10-unit grid of occupiable collider centres.</summary>
    internal static Dictionary<(int X, int Y), int> PathDistances(FixedPoint from)
    {
        var start = Cell(from);
        var dist = new Dictionary<(int, int), int> { [start] = 0 };
        var queue = new Queue<(int X, int Y)>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                var n = (X: c.X + dx, Y: c.Y + dy);
                if (dist.ContainsKey(n)
                    || !ArenaV1.CanOccupy(n.X * CellUnits * GameRulesV1.FixedOne, n.Y * CellUnits * GameRulesV1.FixedOne))
                {
                    continue;
                }
                dist[n] = dist[c] + CellUnits;
                queue.Enqueue(n);
            }
        }
        return dist;
    }
}
