using System.Collections.Immutable;
using System.Security.Cryptography;
using GhostLap.Rules;
using GhostLap.Simulation;
using GhostLap.Timeline;
using Marrow.Kernel.Canonical;
using Marrow.Timeline;

namespace GhostLap.Tests.Simulation;

/// <summary>Small helpers shared by the rule-kernel tests. They drive the real reducer and codec; nothing is mocked.</summary>
internal static class GameTestKit
{
    public static int U(int units) => GameRulesV1.ToFixed(units);

    /// <summary>The Timeline state hash definition: SHA-256 over (stateSchemaVersion, canonical bytes).</summary>
    public static string Hash(GameState state) => Convert.ToHexString(SHA256.HashData(
        new CanonicalWriter().WriteString(GhostLapTimelineVersions.StateSchemaVersion)
            .WriteBytes(GameStateCodec.Instance.EncodeCanonical(state)).ToArray())).ToLowerInvariant();

    /// <summary>A checkpoint as the Timeline stores it: canonical state bytes decoded again.</summary>
    public static GameState Checkpoint(GameState state) =>
        GameStateCodec.Instance.Decode(GameStateCodec.Instance.EncodeCanonical(state));

    public static IReadOnlyList<OrderedInput<GameInput>> Ordered(params GameInput[] inputs) =>
        [.. inputs.Select((g, i) => new OrderedInput<GameInput>($"t{g.Tick}-s{g.Slot}-q{g.Sequence}", i, g))];

    public static GameState Step(GameState state, GameContext context, params GameInput[] inputs) =>
        GhostLapTransition.Instance.Apply(state, state.Tick + 1, Ordered(inputs), context);

    public static GameInput In(GameState before, byte slot, int seq, int mx, int my, bool burst = false) =>
        new(before.Tick + 1, slot, seq, (sbyte)mx, (sbyte)my, burst);

    /// <summary>A world at <paramref name="tick"/> with the given players and nothing collected; candles come from the plan.</summary>
    public static GameState At(GameContext context, long tick, params PlayerState[] players) =>
        GameState.Initial(context) with
        {
            Tick = tick,
            Players = [.. players],
            ActiveCandleIds = context.Plan.ActiveIdsAt(tick),
        };

    public static PlayerState Player(byte slot, int xFixed, int yFixed, int charge = 0, long deadline = 0, long distance = 0) =>
        new(slot, slot, xFixed, yFixed, distance) { Charge = charge, OverlitDeadlineTick = deadline };

    /// <summary>The first candle (in id order) the plan has active at <paramref name="tick"/>.</summary>
    public static CandleSpec CandleActiveAt(GameContext context, long tick) =>
        context.Plan.ActiveAt(tick).First();

    /// <summary>True when a player standing here is legal and touches no candle site, so it cannot pick anything up by accident.</summary>
    public static bool ClearSpot(int xFixed, int yFixed) =>
        ArenaV1.CanOccupy(xFixed, yFixed) && ArenaV1.CandleSites.All(s =>
            Math.Abs(s.X - xFixed) >= GameRulesV1.ColliderHalfExtentFixed + GameRulesV1.CandleHalfExtentFixed
            || Math.Abs(s.Y - yFixed) >= GameRulesV1.ColliderHalfExtentFixed + GameRulesV1.CandleHalfExtentFixed);

    public static ImmutableArray<GameEvent> EventsOf(GameState s, GameEventKind kind) => [.. s.Events.Where(e => e.Kind == kind)];
}
