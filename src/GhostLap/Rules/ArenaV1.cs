using System.Collections.Immutable;

namespace GhostLap.Rules;

/// <summary>Axis-aligned rectangle in fixed-point world coordinates, half-open: x in [X0, X1), y in [Y0, Y1). Touching is not overlap.</summary>
public readonly record struct FixedRect(int X0, int Y0, int X1, int Y1)
{
    public bool Overlaps(FixedRect other) => X0 < other.X1 && other.X0 < X1 && Y0 < other.Y1 && other.Y0 < Y1;
}

/// <summary>A fixed-point world position (a collider centre or a candle site).</summary>
public readonly record struct FixedPoint(int X, int Y);

/// <summary>
/// Layout <c>arena-v1</c>: 1600 x 900 world units, nine short obstacles, authored spawn sockets and candle sites.
/// The obstacle set and the candle site set are point-symmetric around (800, 450).
/// All data is authored here as integers; nothing is computed with floats or read from a clock.
/// </summary>
public static class ArenaV1
{
    private static readonly (int X0, int Y0, int X1, int Y1)[] ObstacleUnits =
    [
        (400, 200, 560, 240),   // top-left bar
        (1040, 200, 1200, 240), // top-right bar
        (400, 660, 560, 700),   // bottom-left bar
        (1040, 660, 1200, 700), // bottom-right bar
        (300, 380, 340, 520),   // left post
        (1260, 380, 1300, 520), // right post
        (760, 410, 840, 490),   // centre block
        (780, 160, 820, 280),   // top stub
        (780, 620, 820, 740),   // bottom stub
    ];

    // Sockets are collider centres in world units. Solo (1 player) uses slot 0 of the 2-player layout.
    private static readonly (int X, int Y)[] Sockets2 = [(200, 450), (1400, 450)];
    private static readonly (int X, int Y)[] Sockets3 = [(800, 80), (400, 780), (1200, 780)];
    private static readonly (int X, int Y)[] Sockets4 = [(120, 120), (1480, 120), (120, 780), (1480, 780)];

    // Twelve authored sites; the other twelve are their point mirrors (1600 - x, 900 - y).
    private static readonly (int X, int Y)[] SiteHalfUnits =
    [
        (300, 120), (250, 300), (480, 320), (640, 200), (640, 340), (400, 450),
        (620, 450), (250, 600), (480, 580), (640, 700), (560, 780), (700, 600),
    ];

    public static ImmutableArray<FixedRect> Obstacles { get; } =
        [.. ObstacleUnits.Select(o => new FixedRect(
            GameRulesV1.ToFixed(o.X0), GameRulesV1.ToFixed(o.Y0), GameRulesV1.ToFixed(o.X1), GameRulesV1.ToFixed(o.Y1)))];

    /// <summary>The 24 candle sites in stable order: the authored twelve, then their mirrors in the same order.</summary>
    public static ImmutableArray<FixedPoint> CandleSites { get; } =
    [
        .. SiteHalfUnits.Select(s => new FixedPoint(GameRulesV1.ToFixed(s.X), GameRulesV1.ToFixed(s.Y))),
        .. SiteHalfUnits.Select(s => new FixedPoint(
            GameRulesV1.ToFixed(GameRulesV1.WorldWidthUnits - s.X), GameRulesV1.ToFixed(GameRulesV1.WorldHeightUnits - s.Y))),
    ];

    /// <summary>Spawn sockets for 1 to 4 players, indexed by slot.</summary>
    public static ImmutableArray<FixedPoint> SpawnSockets(int playerCount)
    {
        var units = playerCount switch
        {
            1 or 2 => Sockets2,
            3 => Sockets3,
            4 => Sockets4,
            _ => throw new ArgumentOutOfRangeException(nameof(playerCount), playerCount, "Player count must be 1 to 4."),
        };
        var take = playerCount == 1 ? 1 : units.Length;
        return [.. units.Take(take).Select(s => new FixedPoint(GameRulesV1.ToFixed(s.X), GameRulesV1.ToFixed(s.Y)))];
    }

    public static FixedRect ColliderAt(int xFixed, int yFixed) => new(
        xFixed - GameRulesV1.ColliderHalfExtentFixed, yFixed - GameRulesV1.ColliderHalfExtentFixed,
        xFixed + GameRulesV1.ColliderHalfExtentFixed, yFixed + GameRulesV1.ColliderHalfExtentFixed);

    /// <summary>True when a player collider centred here lies fully inside the world and overlaps no obstacle.</summary>
    public static bool CanOccupy(int xFixed, int yFixed)
    {
        var h = GameRulesV1.ColliderHalfExtentFixed;
        if (xFixed < h || xFixed > GameRulesV1.WorldWidthFixed - h || yFixed < h || yFixed > GameRulesV1.WorldHeightFixed - h)
        {
            return false;
        }
        var box = ColliderAt(xFixed, yFixed);
        foreach (var obstacle in Obstacles)
        {
            if (box.Overlaps(obstacle))
            {
                return false;
            }
        }
        return true;
    }
}
