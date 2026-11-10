using GhostLap.Rules;
using GhostLap.Simulation;
using Xunit;
using Xunit.Abstractions;
using static GhostLap.Tests.Simulation.GameTestKit;

namespace GhostLap.Tests.Simulation;

/// <summary>
/// Complete 2700-tick greybox rounds through the real reducer. Inputs come from <see cref="CandleSeekingDriver"/>, which only
/// produces inputs (move vector and Burst press), like a keyboard would.
/// </summary>
public sealed class FullGameTests(ITestOutputHelper output)
{
    private static (GameState Final, List<GameInput[]> Log, List<GameState> Trace) Play(GameContext ctx)
    {
        var driver = new CandleSeekingDriver(ctx.PlayerCount);
        var s = GameState.Initial(ctx);
        var log = new List<GameInput[]>();
        var trace = new List<GameState> { s };
        while (!s.IsFinal)
        {
            var inputs = driver.Inputs(s, ctx);
            log.Add(inputs);
            s = Step(s, ctx, inputs);
            trace.Add(s);
        }
        return (s, log, trace);
    }

    private static GameState Replay(GameContext ctx, GameState from, List<GameInput[]> log)
    {
        var s = from;
        while (!s.IsFinal)
        {
            s = Step(s, ctx, log[(int)s.Tick]);
        }
        return s;
    }

    [Theory]
    [InlineData(42L, 1)]
    [InlineData(7L, 2)]
    [InlineData(1234L, 4)]
    public void A_full_round_plays_every_rule_finalizes_once_and_replays_to_the_same_hash(long seed, int players)
    {
        var ctx = GameContext.Create(seed, players);
        var (final, log, trace) = Play(ctx);
        var all = trace.SelectMany(t => t.Events).ToList();
        int Count(GameEventKind k) => all.Count(e => e.Kind == k);
        output.WriteLine(
            $"seed {seed} players {players}: pickups {Count(GameEventKind.CandleCollected)}, overlit {Count(GameEventKind.OverlitStarted)}, " +
            $"manual {Count(GameEventKind.BurstManual)}, auto {Count(GameEventKind.BurstAuto)}, hits {Count(GameEventKind.BurstHit)}, " +
            $"contact {Count(GameEventKind.ContactDrain)}, totals [{string.Join(", ", final.Players.Select(p => $"{p.MovementPoints}+{p.PickupPoints}-{p.PenaltyPoints}={p.TotalScore}"))}]");

        Assert.Equal(GameRulesV1.RoundTicks, final.Tick);
        Assert.Equal(1, Count(GameEventKind.RoundFinished));
        Assert.Equal(GameEventKind.RoundFinished, final.Events[^1].Kind);
        Assert.True(Count(GameEventKind.CandleCollected) >= 4);
        Assert.True(Count(GameEventKind.OverlitStarted) >= 1);
        Assert.True(Count(GameEventKind.BurstManual) + Count(GameEventKind.BurstAuto) >= 1);
        Assert.Equal(final.Collected.Length, Count(GameEventKind.CandleCollected));
        Assert.Equal(final.Collected.Length, final.Players.Sum(p => p.Pickups));

        var result = final.FinalResult!;
        var top = final.Players.Max(p => p.TotalScore);
        Assert.Equal(final.Players.Where(p => p.TotalScore == top).Select(p => p.Slot), result.Winners);
        Assert.All(result.Scores, r => Assert.Equal(r.MovementPoints + r.PickupPoints - r.PenaltyPoints, r.TotalScore));
        Assert.All(trace, t => Assert.True(t.Players.All(p => p.Charge is >= 0 and <= GameRulesV1.MaxCharge && p.TotalScore >= 0)));

        // The recorded inputs alone reproduce the round, from the start and from a checkpoint taken mid-Overlit if one exists.
        Assert.Equal(Hash(final), Hash(Replay(ctx, GameState.Initial(ctx), log)));
        var cut = trace.FirstOrDefault(t => t.Players.Any(p => p.IsOverlit) && t.Collected.Length > 0) ?? trace[1350];
        Assert.Equal(Hash(final), Hash(Replay(ctx, Checkpoint(cut), log)));
        Assert.Throws<GhostLapTransitionException>(() => Step(final, ctx));
    }
}
