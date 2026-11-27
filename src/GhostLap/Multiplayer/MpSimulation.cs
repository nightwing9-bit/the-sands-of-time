using System.Collections.Immutable;
using System.Security.Cryptography;
using GhostLap.Rules;
using GhostLap.Simulation;
using GhostLap.Timeline;
using Marrow.Kernel.Canonical;
using Marrow.Timeline;

namespace GhostLap.Multiplayer;

/// <summary>
/// The pinned facts of one multiplayer match, taken from its sealed round configuration. Only what the reducer needs:
/// rules identity, player count, seed and the candle plan derived from it. The plan is independent of roster and of any action (R10.12).
/// </summary>
public sealed class MpMatchContext
{
    private MpMatchContext(Guid matchId, long seed, int playerCount, string rulesId)
    {
        MatchId = matchId;
        Seed = seed;
        PlayerCount = playerCount;
        RulesId = rulesId;
        Plan = CandlePlan.Create(seed);
    }

    public Guid MatchId { get; }

    public long Seed { get; }

    public int PlayerCount { get; }

    public string RulesId { get; }

    public CandlePlan Plan { get; }

    /// <summary>
    /// Validates the sealed identity against the implemented profiles. A config naming any other rules, layout,
    /// tick budget or spawn assignment is refused rather than reinterpreted (R10.12).
    /// </summary>
    public static MpMatchContext From(SealedRoundConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!MpRulesProfile.Supports(config.RulesId) || config.LayoutId != MpRulesProfile.LayoutId
            || config.RulesHash != MpRulesProfile.HashFor(config.RulesId) || config.LayoutFingerprint != MpRulesProfile.LayoutFingerprint
            || config.TargetTicks != MpRulesProfile.TargetTicks)
        {
            throw new ArgumentException("The sealed round names a rules/layout identity this build does not implement.", nameof(config));
        }
        var n = config.Slots.Count;
        if (n is < MultiplayerOptions.MinCompetitorsFixed or > MultiplayerOptions.MaxCompetitorsCeiling)
        {
            throw new ArgumentException("A multiplayer match has 2 to 4 slots.", nameof(config));
        }
        var sockets = ArenaV1.SpawnSockets(n);
        for (var i = 0; i < n; i++)
        {
            var s = config.Slots[i];
            if (s.Slot != i || s.SpawnXFixed != sockets[i].X || s.SpawnYFixed != sockets[i].Y)
            {
                throw new ArgumentException("The sealed slot/spawn assignment does not match arena-v1.", nameof(config));
            }
        }
        return new MpMatchContext(config.MatchId, config.Seed, n, config.RulesId);
    }

    /// <summary>For tests and tools: a context with the given seed and player count.</summary>
    public static MpMatchContext Create(long seed, int playerCount, string rulesId = MpRulesProfile.RulesId)
    {
        if (playerCount is < MultiplayerOptions.MinCompetitorsFixed or > MultiplayerOptions.MaxCompetitorsCeiling)
        {
            throw new ArgumentOutOfRangeException(nameof(playerCount), playerCount, "A multiplayer match has 2 to 4 slots.");
        }
        if (!MpRulesProfile.Supports(rulesId))
        {
            throw new ArgumentException("Unknown multiplayer rules identity.", nameof(rulesId));
        }
        return new MpMatchContext(Guid.Empty, seed, playerCount, rulesId);
    }
}

/// <summary>
/// Complete authoritative multiplayer world at the end of <see cref="Tick"/>: the shared ghost-v1 world record plus one
/// forfeit tick per slot (0 = taking part). Not archived anywhere (no multiplayer per-tick journal); hashed for determinism checks.
/// </summary>
public sealed record MpMatchState(GameState World, ImmutableArray<long> ForfeitTicks)
{
    public string RulesId { get; init; } = MpRulesProfile.RulesId;

    public long Tick => World.Tick;

    public bool IsFinal => World.IsFinal;

    public bool IsForfeited(int slot) => ForfeitTicks[slot] != 0;

    public static MpMatchState Initial(MpMatchContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var world = GameState.Initial(GameContext.Create(context.Seed, context.PlayerCount));
        return new MpMatchState(world, [.. new long[context.PlayerCount]]) { RulesId = context.RulesId };
    }
}

/// <summary>
/// The versioned multiplayer transition: phase 0 applies forfeits scheduled for this tick, then the shared phase
/// implementation runs the sealed conflict rules with the forfeited slots masked. There is no second physics engine. Pure: no clock, no random
/// source, no I/O; equal inputs give equal states.
/// </summary>
public static class MpRules
{
    /// <param name="inputs">The admitted inputs of tick <c>previous.Tick + 1</c>, in admission order.</param>
    /// <param name="forfeitNow">Slots whose forfeit takes effect on this tick (task 10.3 schedules them; empty otherwise).</param>
    public static MpMatchState Step(MpMatchState previous, IReadOnlyList<GameInput> inputs, IReadOnlyCollection<int> forfeitNow, MpMatchContext context)
        => StepWithPhases(previous, inputs, forfeitNow, context,
            context.RulesId == MpRulesProfile.CurrentRulesId ? CompetitivePhases.All : CompetitivePhases.Legacy);

    internal static MpMatchState StepWithPhases(MpMatchState previous, IReadOnlyList<GameInput> inputs,
        IReadOnlyCollection<int> forfeitNow, MpMatchContext context, CompetitivePhases phases)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(forfeitNow);
        ArgumentNullException.ThrowIfNull(context);
        var n = previous.World.Players.Length;
        if (previous.RulesId != context.RulesId)
        {
            throw new GhostLapTransitionException("State and context have different multiplayer rules identities.");
        }
        if (n != context.PlayerCount || previous.ForfeitTicks.Length != n
            || previous.World.PairContactReadyTicks.Length != GameState.PairCount(n))
        {
            throw new GhostLapTransitionException("State does not match the match context's player count.");
        }
        var position = previous.Tick + 1;
        if (position > GameRulesV1.RoundTicks)
        {
            throw new GhostLapTransitionException($"Tick {position} is past the final tick {GameRulesV1.RoundTicks}.");
        }

        // Phase 0: participation. A forfeited slot keeps its frozen score and charge as stats; its pending Overlit deadline is
        // cleared without a Burst and its effective input is released (ADR-P1-22 D6/D9).
        var forfeits = previous.ForfeitTicks.ToArray();
        var world = previous.World;
        if (forfeitNow.Count > 0)
        {
            var players = world.Players.ToArray();
            foreach (var slot in forfeitNow)
            {
                if (slot < 0 || slot >= n)
                {
                    throw new GhostLapTransitionException($"Forfeit of missing slot {slot}.");
                }
                if (forfeits[slot] != 0)
                {
                    continue;
                }
                forfeits[slot] = position;
                players[slot] = players[slot] with
                {
                    OverlitDeadlineTick = 0,
                    HeldMoveX = 0,
                    HeldMoveY = 0,
                    HoldUntilTick = 0,
                    BurstLatch = false,
                };
            }
            world = world with { Players = [.. players] };
        }
        var mask = new bool[n];
        for (var i = 0; i < n; i++)
        {
            mask[i] = forfeits[i] != 0;
        }
        var ordered = new OrderedInput<GameInput>[inputs.Count];
        for (var i = 0; i < inputs.Count; i++)
        {
            var g = inputs[i];
            ordered[i] = new OrderedInput<GameInput>($"mp-t{g.Tick}-s{g.Slot}-q{g.Sequence}", i, g);
        }
        var next = GhostLapPhases.Resolve(world, position, ordered, context.Plan, mask, context.Seed, phases);
        return previous with { World = next, ForfeitTicks = [.. forfeits] };
    }
}

/// <summary>
/// Canonical encoding of <see cref="MpMatchState"/> under its sealed rules schema: the rules id, the world
/// encoded with the ghost-v1 state layout (embedded as bytes, so ghost-v1's codec is reused and never changed), then the forfeit
/// tick of every slot. Encode only: multiplayer state is never archived or restored from bytes in this task; the hash is
/// the determinism check (same seed and applied inputs give the same hash).
/// </summary>
public static class MpStateCodec
{
    public static byte[] EncodeCanonical(MpMatchState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var w = new CanonicalWriter()
            .WriteString(state.RulesId)
            .WriteBytes(GameStateCodec.Instance.EncodeCanonical(state.World))
            .WriteCount(state.ForfeitTicks.Length);
        foreach (var f in state.ForfeitTicks)
        {
            w.WriteInt64(f);
        }
        return w.ToArray();
    }

    /// <summary>SHA-256 over (state schema, canonical bytes), lower-case hex. Same definition shape as the ghost-v1 Timeline hash.</summary>
    public static string Hash(MpMatchState state) => Convert.ToHexStringLower(SHA256.HashData(
        new CanonicalWriter()
            .WriteString(state.RulesId == MpRulesProfile.CurrentRulesId ? MpRulesProfile.CurrentStateSchema : MpRulesProfile.StateSchema)
            .WriteBytes(EncodeCanonical(state)).ToArray()));
}
