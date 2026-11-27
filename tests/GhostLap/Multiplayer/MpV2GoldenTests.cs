using GhostLap.Multiplayer;
using GhostLap.Rules;
using GhostLap.Simulation;
using Xunit;

namespace GhostLap.Tests.Multiplayer;

public sealed class MpV2GoldenTests
{
    [Theory]
    [InlineData(2, "7a1496ed99bd7431d29d31c82a4e3b2b1f492878f640a300720bc522355c2ee9")]
    [InlineData(3, "c75b22780216152253a05f2c6834b19f01cde3bc5e9361ef9a7fe61d53acb846")]
    [InlineData(4, "4a2a569c3f7c15299455f7b703ac4252ac9628c685af8841b205a38ea53b3630")]
    public void The_original_audit_replay_keeps_its_v1_hash(int players, string historical)
    {
        var scripts = Enumerable.Range(0, players).Select(_ => new RoutedSeeker(true)).ToArray();
        Assert.Equal(historical, Playtest.Run(1000, scripts, "historical-audit").StateHash);
    }

    [Theory]
    [InlineData(2, "2f8c19758858a0e2eef034c372ee76ada3e9bb7397ab64cdce9e8162950f147d")]
    [InlineData(3, "45a71b7c02e21401fca811c65e91835ffd48642b90d58c496a26f1bd38e67ec0")]
    [InlineData(4, "1e516815735fbf31636b7a3143f3a9e1aa477c9aac87743107db972124b27c20")]
    public void Final_tick_golden_covers_competitive_conflicts_and_replays_independently_of_input_order(int n, string golden)
    {
        const int tick = GameRulesV1.RoundTicks;
        var key = Enumerable.Repeat((byte)23, 32).ToArray();
        var roster = Enumerable.Range(0, n).Select(i => new RosterEntry($"m{i}", $"guest:{i:D8}-0000-0000-0000-000000000000")).ToArray();
        var config = RoundSealing.Seal(key, Guid.Empty, 1, roster, new MultiplayerOptions(), MatchTestKit.Generation, MpRulesProfile.CurrentRulesId);
        var context = MpMatchContext.From(config);
        var initial = MpMatchState.Initial(context);
        var site = context.Plan.ActiveAt(tick).First();
        var players = initial.World.Players.Select(p => p with
        {
            XFixed = site.Site.X,
            YFixed = site.Site.Y,
            Charge = n == 3 && p.Slot == 2 ? 2 : GameRulesV1.MaxCharge,
            OverlitDeadlineTick = tick,
            DistanceFixed = (n == 2 ? 0 : 100L) * GameRulesV1.MovementScoreUnitFixed,
        }).ToArray();
        var before = initial with
        {
            World = initial.World with { Tick = tick - 1, Players = [.. players] },
        };
        GameInput[] presses = [new(tick, 0, 2, 0, 0, true), new(tick, 1, 1, 0, 0, true), new(tick, 0, 1, 0, 0, false)];
        int[] forfeits = n == 4 ? [3] : [];
        var after = MpRules.Step(before, presses, forfeits, context);
        var replay = MpRules.Step(before, presses.Reverse().ToArray(), forfeits, MpMatchContext.From(config));
        Assert.Equal(MpStateCodec.Hash(after), MpStateCodec.Hash(replay));
        Assert.Equal(2, after.World.Events.Count(e => e.Kind == GameEventKind.BurstManual));
        Assert.Equal(n == 4 ? 1 : 0, after.World.Events.Count(e => e.Kind == GameEventKind.BurstAuto));
        Assert.Equal(n == 3 ? 2 : 0, after.World.Events.Count(e => e.Kind == GameEventKind.ContactDrain));
        Assert.Single(after.World.Events, e => e.Kind == GameEventKind.CandleCollected && e.CandleId == site.Id);
        Assert.DoesNotContain(after.World.Events, e => e.Kind == GameEventKind.OverlitCancelled);
        if (n == 4)
        {
            Assert.Equal(tick, after.ForfeitTicks[3]);
            Assert.DoesNotContain(after.World.Events, e => e.Kind != GameEventKind.RoundFinished && e.Slot == 3);
        }
        var stats = new MpMatchStats(n);
        stats.Add(after.World.Events);
        var result = MatchResults.Build(config, after, stats);
        Assert.Equal(MpRulesProfile.CurrentRulesId, result.Document.RulesId);
        if (n == 2)
        {
            Assert.All(result.Document.Players, p => Assert.Equal(1, p.CompetitivePlacement));
        }
        Assert.True(golden == result.Document.FinalStateHash, $"actual {n}P = {result.Document.FinalStateHash}");
    }

    [Fact]
    public void Shared_contact_cannot_create_a_new_weaker_side_in_an_equal_pair()
    {
        var context = MpMatchContext.Create(21, 3, MpRulesProfile.CurrentRulesId);
        var initial = MpMatchState.Initial(context);
        var state = initial with
        {
            World = initial.World with
            {
                Players = [.. initial.World.Players.Select(p => p with
                {
                    XFixed = GameRulesV1.ToFixed(650),
                    YFixed = GameRulesV1.ToFixed(100),
                    Charge = p.Slot == 1 ? 3 : 2,
                })],
            },
        };
        var outcome = MpRules.Step(state, [], [], context);
        Assert.Equal(new[] { 1, 3, 1 }, outcome.World.Players.Select(p => p.Charge));
        Assert.Equal(2, outcome.World.Events.Count(e => e.Kind == GameEventKind.ContactDrain));
        Assert.Equal(0, outcome.World.PairContactReadyTicks[GameState.PairIndex(0, 2, 3)]);
    }

}
