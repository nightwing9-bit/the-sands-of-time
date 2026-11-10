namespace GhostLap.Rules;

/// <summary>
/// Constants of GameRules <c>ghost-v1</c> / layout <c>arena-v1</c>. Every authoritative quantity is an integer.
/// Positions and speeds are fixed point: 1 world unit = <see cref="FixedOne"/> (1024) fixed units.
/// A change to any value here is a new rules version, never an edit of <c>ghost-v1</c> after histories exist.
/// </summary>
public static class GameRulesV1
{
    public const string RulesVersion = "ghost-v1";
    public const string LayoutVersion = "arena-v1";

    public const int TickRate = 30;
    public const int RoundTicks = 2700;

    public const int FixedShift = 10;
    public const int FixedOne = 1 << FixedShift;

    public const int WorldWidthUnits = 1600;
    public const int WorldHeightUnits = 900;
    public const int WorldWidthFixed = WorldWidthUnits * FixedOne;
    public const int WorldHeightFixed = WorldHeightUnits * FixedOne;

    public const int MaxPlayers = 4;
    public const int MascotCount = 4;

    /// <summary>Equal axis-aligned square collider for every mascot: half extent 24 units (48 x 48).</summary>
    public const int ColliderHalfExtentFixed = 24 * FixedOne;

    /// <summary>10 world units per tick (300 units/s). A cardinal move; always shorter than the collider, so no tunnelling.</summary>
    public const int CardinalStepFixed = 10 * FixedOne;

    /// <summary>
    /// Per-axis component of a diagonal move: round(CardinalStepFixed / sqrt(2)) = round(7240.96) = 7241.
    /// A fixed lookup constant, so authoritative simulation never computes a square root of a float or a trig value.
    /// </summary>
    public const int DiagonalStepFixed = 7241;

    public const int CandleLifetimeTicks = 210;
    public const int CandleTelegraphTicks = 30;
    /// <summary>Mean activation spacing. Lifetime / spacing = 10 candles active on average (target band 8 to 12).</summary>
    public const int CandleSpacingTicks = 21;
    /// <summary>Exclusive upper bound of the per-candle activation jitter in ticks.</summary>
    public const int CandleJitterTicks = 5;

    /// <summary>A candle is a 24 x 24 square around its site; a player collects it when their colliders overlap (touching is not overlap).</summary>
    public const int CandleHalfExtentFixed = 12 * FixedOne;

    // ---- Soulfire charge, Overlit and score (task 6.2). Initial testable defaults, tuned only by a new rules version. ----

    /// <summary>Soulfire charge is always in 0..MaxCharge. Reaching MaxCharge is Overlit (armed).</summary>
    public const int MaxCharge = 4;

    /// <summary>Score for each collected candle, also while already at MaxCharge.</summary>
    public const int CandlePickupPoints = 25;

    /// <summary>Overlit window: reaching MaxCharge at tick t sets the deadline t + 240 (8 s); an unused Overlit auto-Bursts on the deadline tick.</summary>
    public const int OverlitTicks = 240;

    /// <summary>Charge the weaker player loses on an eligible contact. Contact never changes score.</summary>
    public const int ContactChargeLoss = 1;

    /// <summary>A pair that had an eligible contact at tick t is next eligible at tick t + 45 (1.5 s).</summary>
    public const int ContactCooldownTicks = 45;

    /// <summary>
    /// Burst radius between collider centres (inclusive), 160 world units. <c>ghost-v1</c> has no wall occlusion:
    /// obstacles do not shield a target (ADR-P1-09). Changing that is a new rules version.
    /// </summary>
    public const int BurstRadiusFixed = 160 * FixedOne;

    /// <summary>Charge each Burst target loses (clamped at zero). The owner's charge resets to zero.</summary>
    public const int BurstChargeLoss = 2;

    /// <summary>Score each Burst target loses, clamped so a total never goes below zero. There is no attacker reward.</summary>
    public const int BurstPenaltyPoints = 90;

    /// <summary>
    /// Movement points are <c>floor(DistanceFixed / MovementScoreUnitFixed)</c>: one point per 16 world units of
    /// authoritative post-collision travel. The cumulative fixed-point distance is kept, never the floored points.
    /// </summary>
    public const int MovementScoreUnitFixed = 16 * FixedOne;

    // ---- Effective input policy (task 6.2). ----

    /// <summary>A movement vector accepted at tick t stays effective for at most six further ticks without a newer input, then neutral.</summary>
    public const int InputHoldTicks = 6;

    /// <summary>An input whose client tick is more than this many ticks behind the next server tick is rejected as late.</summary>
    public const int InputMaxLateTicks = 4;

    /// <summary>An input whose client tick is more than this many ticks ahead of the next server tick is rejected.</summary>
    public const int InputMaxLeadTicks = 2;

    public static int ToFixed(int units) => checked(units * FixedOne);

    /// <summary>Floor of the square root of a non-negative integer, by integer Newton iteration. No floating point.</summary>
    public static long IntegerSqrt(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        if (value < 2)
        {
            return value;
        }
        var x = value;
        var y = (x + 1) / 2;
        while (y < x)
        {
            x = y;
            y = (x + value / x) / 2;
        }
        return x;
    }
}
