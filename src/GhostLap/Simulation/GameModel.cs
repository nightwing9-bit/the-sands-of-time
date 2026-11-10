using System.Collections.Immutable;
using GhostLap.Rules;

namespace GhostLap.Simulation;

/// <summary>
/// One player's complete authoritative state at the end of a tick.
/// <para>Physics: position and <see cref="DistanceFixed"/>, the cumulative post-collision distance in fixed units.
/// Movement points are derived from the full distance, so the sub-point remainder is never lost.</para>
/// <para>Soulfire: <see cref="Charge"/> in 0..4; <see cref="OverlitDeadlineTick"/> is nonzero exactly while charge is 4.</para>
/// <para>Score components: movement (derived), pickups (count x 25) and the clamped penalty actually applied.</para>
/// <para>Effective input: <see cref="LastSequence"/> (-1 before any input), the held vector, which is the vector that moved
/// the player on this tick, its <see cref="HoldUntilTick"/> (0 when neutral) and the Burst edge <see cref="BurstLatch"/>
/// (the last accepted <c>requestBurst</c> value).</para>
/// </summary>
public sealed record PlayerState(byte Slot, byte MascotId, int XFixed, int YFixed, long DistanceFixed)
{
    public int Charge { get; init; }
    public long OverlitDeadlineTick { get; init; }
    public int Pickups { get; init; }
    public int PenaltyPoints { get; init; }
    public int Bursts { get; init; }
    public int BurstHits { get; init; }
    public int LastSequence { get; init; } = -1;
    public sbyte HeldMoveX { get; init; }
    public sbyte HeldMoveY { get; init; }
    public long HoldUntilTick { get; init; }
    public bool BurstLatch { get; init; }

    public bool IsOverlit => Charge == GameRulesV1.MaxCharge;
    public int MovementPoints => checked((int)(DistanceFixed / GameRulesV1.MovementScoreUnitFixed));
    public int PickupPoints => checked(Pickups * GameRulesV1.CandlePickupPoints);
    public int TotalScore => checked(MovementPoints + PickupPoints - PenaltyPoints);
}

/// <summary>A candle this world has collected: by whom and on which tick. Kept for the whole round, ordered by id.</summary>
public sealed record CollectedCandle(string Id, byte Slot, long Tick);

public enum GameEventKind : byte
{
    /// <summary>Slot collected <see cref="GameEvent.CandleId"/>; Value = points awarded.</summary>
    CandleCollected = 1,
    /// <summary>Slot reached full charge; Value = deadline tick.</summary>
    OverlitStarted = 2,
    /// <summary>Slot (weaker) touched OtherSlot (stronger); Value = charge actually lost.</summary>
    ContactDrain = 3,
    /// <summary>Slot fired a requested Burst; Value = number of targets hit.</summary>
    BurstManual = 4,
    /// <summary>Slot's Overlit deadline fired a Burst; Value = number of targets hit.</summary>
    BurstAuto = 5,
    /// <summary>Slot was hit by OtherSlot's Burst; Value = score penalty actually applied.</summary>
    BurstHit = 6,
    /// <summary>Slot dropped below full charge before bursting.</summary>
    OverlitCancelled = 7,
    /// <summary>The final tick was resolved; Value = winning total score.</summary>
    RoundFinished = 8,
}

/// <summary>One resolved event of a tick, in the stable phase order the reducer emitted it.</summary>
public sealed record GameEvent(GameEventKind Kind, byte Slot, byte OtherSlot = 0, int Value = 0, string CandleId = "");

/// <summary>Score breakdown of one player at the end of the round.</summary>
public sealed record PlayerScore(
    byte Slot, int MovementPoints, int PickupPoints, int PenaltyPoints, int TotalScore, int Pickups, int Bursts, int BurstHits);

/// <summary>Final result: highest total score wins; several winners means a tie. Remaining charge is not a tiebreaker.</summary>
public sealed record GameResult(ImmutableArray<PlayerScore> Scores, ImmutableArray<byte> Winners)
{
    public bool IsTie => Winners.Length > 1;
}

/// <summary>
/// Complete authoritative world at the end of <see cref="Tick"/>. Tick 0 is the initial state.
/// <see cref="ActiveCandleIds"/> are planned-active candles this world has not collected. <see cref="PairContactReadyTicks"/>
/// has one entry per slot pair in (0,1),(0,2),(0,3),(1,2),(1,3),(2,3) order: 0, or the first tick that pair may contact again.
/// <see cref="Events"/> are the events of this tick only.
/// </summary>
public sealed record GameState(long Tick, ImmutableArray<PlayerState> Players, ImmutableArray<string> ActiveCandleIds)
{
    public ImmutableArray<CollectedCandle> Collected { get; init; } = [];
    public ImmutableArray<long> PairContactReadyTicks { get; init; } = [];
    public ImmutableArray<GameEvent> Events { get; init; } = [];

    public bool IsFinal => Tick == GameRulesV1.RoundTicks;

    /// <summary>The score-breakdown result, available only once the final tick has been resolved.</summary>
    public GameResult? FinalResult
    {
        get
        {
            if (!IsFinal)
            {
                return null;
            }
            var scores = Players.Select(p => new PlayerScore(
                p.Slot, p.MovementPoints, p.PickupPoints, p.PenaltyPoints, p.TotalScore, p.Pickups, p.Bursts, p.BurstHits))
                .ToImmutableArray();
            var top = scores.Max(s => s.TotalScore);
            return new GameResult(scores, [.. scores.Where(s => s.TotalScore == top).Select(s => s.Slot)]);
        }
    }

    public static int PairCount(int players) => players * (players - 1) / 2;

    /// <summary>Index of pair (a, b), a &lt; b, in <see cref="PairContactReadyTicks"/>.</summary>
    public static int PairIndex(int a, int b, int players)
    {
        if (a < 0 || a >= b || b >= players)
        {
            throw new ArgumentOutOfRangeException(nameof(a), "Pair must satisfy 0 <= a < b < players.");
        }
        return a * players - a * (a + 1) / 2 + (b - a - 1);
    }

    public static GameState Initial(GameContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var sockets = ArenaV1.SpawnSockets(context.PlayerCount);
        var players = ImmutableArray.CreateBuilder<PlayerState>(sockets.Length);
        for (var slot = 0; slot < sockets.Length; slot++)
        {
            players.Add(new PlayerState((byte)slot, context.Mascots[slot], sockets[slot].X, sockets[slot].Y, 0));
        }
        return new GameState(0, players.MoveToImmutable(), context.Plan.ActiveIdsAt(0))
        {
            PairContactReadyTicks = [.. new long[PairCount(sockets.Length)]],
        };
    }
}

/// <summary>
/// One slot's accepted input for one tick: <c>(tick, slot, sequence, moveX, moveY, requestBurst)</c>.
/// Move components are -1, 0 or 1. There is deliberately no position field, so a client cannot claim a location.
/// <c>requestBurst</c> is the pressed state of the Burst control; a Burst is requested only on its rising edge.
/// </summary>
public sealed record GameInput(long Tick, byte Slot, int Sequence, sbyte MoveX, sbyte MoveY, bool RequestBurst)
{
    public static GameInput Neutral(long tick, byte slot, int sequence = 0) => new(tick, slot, sequence, 0, 0, false);
}

/// <summary>
/// The pinned context of a game: rules and layout versions, seed, player count and mascot per slot.
/// The candle plan is derived from the seed once per context instance and is not part of equality.
/// </summary>
public sealed class GameContext : IEquatable<GameContext>
{
    private readonly Lazy<CandlePlan> _plan;

    private GameContext(long seed, int playerCount, ImmutableArray<byte> mascots)
    {
        Seed = seed;
        PlayerCount = playerCount;
        Mascots = mascots;
        _plan = new Lazy<CandlePlan>(() => CandlePlan.Create(seed), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string RulesVersion => GameRulesV1.RulesVersion;
    public string LayoutVersion => GameRulesV1.LayoutVersion;
    public long Seed { get; }
    public int PlayerCount { get; }
    public ImmutableArray<byte> Mascots { get; }
    public CandlePlan Plan => _plan.Value;

    /// <param name="mascots">One mascot id (0 to 3) per slot; mascots are identity only and never change speed or collider.</param>
    public static GameContext Create(long seed, int playerCount, IEnumerable<byte> mascots)
    {
        ArgumentNullException.ThrowIfNull(mascots);
        if (playerCount is < 1 or > GameRulesV1.MaxPlayers)
        {
            throw new ArgumentOutOfRangeException(nameof(playerCount), playerCount, "Player count must be 1 to 4.");
        }
        var list = mascots.ToImmutableArray();
        if (list.Length != playerCount || list.Any(m => m >= GameRulesV1.MascotCount))
        {
            throw new ArgumentException($"Expected {playerCount} mascot ids in 0..{GameRulesV1.MascotCount - 1}.", nameof(mascots));
        }
        return new GameContext(seed, playerCount, list);
    }

    public static GameContext Create(long seed, int playerCount) =>
        Create(seed, playerCount, Enumerable.Range(0, playerCount).Select(i => (byte)i));

    public bool Equals(GameContext? other) =>
        other is not null && Seed == other.Seed && PlayerCount == other.PlayerCount && Mascots.SequenceEqual(other.Mascots);

    public override bool Equals(object? obj) => obj is GameContext other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Seed, PlayerCount, Mascots.Length);
}
