using GhostLap.Rules;
using GhostLap.Simulation;
using Marrow.Timeline;

namespace GhostLap.Timeline;

/// <summary>
/// Pinned Timeline version identity of Ghost Lap histories and the components registered for it.
/// The reducer version is the GameRules version; a rules change creates a new key and old keys stay registered.
/// </summary>
public static class GhostLapTimelineVersions
{
    public const string DomainId = "ghost-lap";
    public const string InputSchemaVersion = "ghost-input/1";
    public const string StateSchemaVersion = "ghost-state/1";
    public const string ContextSchemaVersion = "ghost-context/1";

    /// <summary>Initial checkpoint cadence in ticks. Adjust only after measuring (Spec 06 design).</summary>
    public const int CheckpointInterval = 60;

    public static TimelineVersionKey V1 { get; } = new(
        DomainId, GameRulesV1.RulesVersion, InputSchemaVersion, StateSchemaVersion, ContextSchemaVersion);

    public static TimelineComponents<GameState, GameInput, GameContext> V1Components { get; } = new(
        V1, GhostLapTransition.Instance, GameStateCodec.Instance, GameInputCodec.Instance, GameContextCodec.Instance);

    /// <summary>Every version a stored Ghost Lap history may pin. Keep old entries when adding new ones.</summary>
    public static IReadOnlyList<TimelineComponents<GameState, GameInput, GameContext>> All { get; } = [V1Components];
}
