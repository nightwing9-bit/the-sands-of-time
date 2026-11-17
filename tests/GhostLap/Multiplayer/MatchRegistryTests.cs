using GhostLap.Contracts.Multiplayer;
using GhostLap.Multiplayer;
using Npgsql;
using Xunit;
using static GhostLap.Tests.Multiplayer.MatchTestKit;

namespace GhostLap.Tests.Multiplayer;

/// <summary>
/// Task 10.2 (R10.15): no receipt, no match. The registry is given a real store whose database is unreachable (a closed local port),
/// or an authority that is not valid; it must never report Admitted, never create a runtime and never accept gameplay.
/// </summary>
public sealed class MatchRegistryTests
{
    private static MatchRegistry Registry(IRealtimeAuthority authority, out NpgsqlDataSource source)
    {
        source = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1;Pooling=false");
        return new MatchRegistry(new MultiplayerOptions(), new ManualTime(), authority, new MatchAuthorityStore(source));
    }

    [Fact]
    public async Task An_unreachable_database_never_starts_a_match()
    {
        var registry = Registry(new SwitchableAuthority(Generation), out var source);
        await using var _ = source;
        var config = Seal(2);
        Assert.Equal(StartAdmissionOutcome.Unavailable, await registry.AdmitAsync(config, CancellationToken.None));
        Assert.Null(registry.StatusOf(config.MatchId));
        Assert.Empty(registry.Matches);
        registry.Activate(config.MatchId, [new SlotControl(0, 1, "c0"), new SlotControl(1, 1, "c1")]); // no runtime: no-op
        var input = new GameplayInput(config.MatchId.ToString("N"), 1, 1, 1, 1, 0, false);
        Assert.Equal(GhostLapFailure.NoRunningMatch, registry.Submit(new AdmittedInput(config.RoomId, config.Slots[0].MemberId, "s", 1, input, "c0")));
    }

    [Fact]
    public async Task Without_valid_authority_or_with_another_generations_seal_admission_is_refused_before_any_database_call()
    {
        var lost = new SwitchableAuthority(Generation);
        lost.Lose();
        var registry = Registry(lost, out var source);
        await using var _ = source;
        Assert.Equal(StartAdmissionOutcome.Unavailable, await registry.AdmitAsync(Seal(3), CancellationToken.None));

        var other = Registry(new SwitchableAuthority(Guid.NewGuid()), out var source2);
        await using var __ = source2;
        Assert.Equal(StartAdmissionOutcome.Unavailable, await other.AdmitAsync(Seal(3), CancellationToken.None));
        Assert.Empty(other.Matches);
    }
}
