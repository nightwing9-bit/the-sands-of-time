using GhostLap.Contracts.Multiplayer;
using GhostLap.Multiplayer;

namespace GhostLap.Tests.Multiplayer;

/// <summary>
/// A real implementation of the authority port whose validity a test controls (the process guard and generation are host concerns;
/// here they are just a flag). Nothing about the engine, reducer or scheduler is substituted.
/// </summary>
internal sealed class SwitchableAuthority(Guid generation) : IRealtimeAuthority
{
    private volatile bool _valid = true;

    public bool IsValid => _valid;

    public Guid GenerationId { get; } = generation;

    public void Lose() => _valid = false;
}

internal static class MatchTestKit
{
    public static readonly Guid Generation = Guid.Parse("33333333-3333-3333-3333-333333333333");

    /// <summary>A real sealed round for <paramref name="players"/> distinct guests, exactly as the room owner seals it.</summary>
    public static SealedRoundConfig Seal(int players, byte secret = 7, MultiplayerOptions? options = null)
    {
        var key = Enumerable.Repeat(secret, 32).ToArray();
        var roster = Enumerable.Range(0, players).Select(i => new RosterEntry($"m{i}", $"guest:{i:D8}-0000-0000-0000-000000000000")).ToArray();
        return RoundSealing.Seal(key, Guid.Parse("11111111-1111-1111-1111-111111111111"), 1, roster, options ?? new MultiplayerOptions(), Generation);
    }

    /// <summary>An engine whose every slot has a controlling connection <c>c{slot}</c> at epoch 1.</summary>
    public static MatchEngine Engine(int players, MultiplayerOptions? options = null, TimeProvider? time = null, byte secret = 7)
    {
        options ??= new MultiplayerOptions { InputsPerSecond = 1000, InputBurst = 1000 };
        var engine = new MatchEngine(Seal(players, secret, options), options, time ?? new ManualTime());
        for (var slot = 0; slot < players; slot++)
        {
            engine.SetController(new SlotControl(slot, 1, $"c{slot}"));
        }
        return engine;
    }

    public static string MemberOf(MatchEngine engine, int slot) => engine.Config.Slots[slot].MemberId;

    /// <summary>The ingress path of one item from slot <paramref name="slot"/>'s current controller (or a given epoch/connection).</summary>
    public static string? Send(
        MatchEngine engine, int slot, int seq, int mx, int my, bool burst = false, long? epoch = null, string? connection = null, long? clientTick = null)
    {
        var control = engine.ControllerOf(slot);
        var e = epoch ?? control.Epoch;
        var input = new GameplayInput(
            engine.Config.MatchId.ToString("N"), e, clientTick ?? engine.State.Tick + 1, seq, (sbyte)mx, (sbyte)my, burst);
        return engine.TryEnqueue(new AdmittedInput(
            engine.Config.RoomId, MemberOf(engine, slot), engine.Config.Slots[slot].Subject, e, input, connection ?? control.ConnectionId ?? ""));
    }
}
