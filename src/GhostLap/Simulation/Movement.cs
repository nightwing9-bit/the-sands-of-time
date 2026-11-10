using GhostLap.Rules;

namespace GhostLap.Simulation;

/// <summary>
/// Fixed-point movement and collision of one square collider. Pure integer arithmetic.
/// The X axis is resolved first and then the Y axis (the deterministic tie-break), so a diagonal move into a wall
/// slides along it and a diagonal move into a corner stops at the corner. Each axis is limited by the nearest
/// blocker along its path, which makes the result independent of the order obstacles are listed.
/// </summary>
public static class Movement
{
    public readonly record struct Result(int XFixed, int YFixed, long DistanceFixed);

    /// <summary>
    /// Moves a collider centred at (<paramref name="x"/>, <paramref name="y"/>) by the input vector.
    /// Components must be -1, 0 or 1. Distance is the integer Euclidean length of the displacement that actually happened.
    /// </summary>
    public static Result Move(int x, int y, int moveX, int moveY)
    {
        if (moveX is < -1 or > 1 || moveY is < -1 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(moveX), "Move components must be -1, 0 or 1.");
        }
        var step = moveX != 0 && moveY != 0 ? GameRulesV1.DiagonalStepFixed : GameRulesV1.CardinalStepFixed;
        var nx = ResolveAxis(x, y, moveX * step, horizontal: true);
        var ny = ResolveAxis(y, nx, moveY * step, horizontal: false);
        long dx = nx - x;
        long dy = ny - y;
        return new Result(nx, ny, GameRulesV1.IntegerSqrt(dx * dx + dy * dy));
    }

    /// <summary>
    /// Moves along one axis. <paramref name="along"/> is the centre coordinate on the moving axis and
    /// <paramref name="across"/> the centre coordinate on the other axis. Returns the new <paramref name="along"/>.
    /// </summary>
    private static int ResolveAxis(int along, int across, int delta, bool horizontal)
    {
        if (delta == 0)
        {
            return along;
        }
        var h = GameRulesV1.ColliderHalfExtentFixed;
        var max = horizontal ? GameRulesV1.WorldWidthFixed : GameRulesV1.WorldHeightFixed;
        var target = Math.Clamp(along + delta, h, max - h);
        foreach (var obstacle in ArenaV1.Obstacles)
        {
            var (lo, hi, crossLo, crossHi) = horizontal
                ? (obstacle.X0, obstacle.X1, obstacle.Y0, obstacle.Y1)
                : (obstacle.Y0, obstacle.Y1, obstacle.X0, obstacle.X1);
            if (!(across - h < crossHi && crossLo < across + h))
            {
                continue; // not in this collider's lane
            }
            if (delta > 0 && lo >= along + h)
            {
                target = Math.Min(target, lo - h);
            }
            else if (delta < 0 && hi <= along - h)
            {
                target = Math.Max(target, hi + h);
            }
        }
        return target;
    }
}
