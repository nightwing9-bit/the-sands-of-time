using GhostLap.Contracts.Multiplayer;
using GhostLap.Multiplayer;
using GhostLap.Rules;
using GhostLap.Simulation;
using Xunit;
using static GhostLap.Tests.Multiplayer.MatchTestKit;

namespace GhostLap.Tests.Multiplayer;

public sealed class TerminalIngressTests
{
    private static MatchEngine FinalEngine(bool invalidRules = false)
    {
        var config = Seal(2);
        var initial = MpMatchState.Initial(MpMatchContext.From(config));
        var tick = GameRulesV1.RoundTicks - 1;
        var armed = initial.World.Players[0] with { Charge = GameRulesV1.MaxCharge, OverlitDeadlineTick = tick + 1 };
        var engine = new MatchEngine(config, new MultiplayerOptions { InputsPerSecond = 1000, InputBurst = 1000, MailboxCapacity = 16 },
            new ManualTime(), initial with
            {
                RulesId = invalidRules ? MpRulesProfile.CurrentRulesId : initial.RulesId,
                World = initial.World with { Tick = tick, Players = [armed, initial.World.Players[1]] },
            });
        engine.SetController(new SlotControl(0, 1, "c0"));
        engine.SetController(new SlotControl(1, 1, "c1"));
        return engine;
    }

    [Fact]
    public void The_last_batch_applies_one_burst_and_refuses_duplicate_and_stale_sequences()
    {
        var engine = FinalEngine();
        Assert.Null(Send(engine, 0, 2, 0, 0, burst: true));
        Assert.Null(Send(engine, 0, 2, 1, 0, burst: true));
        Assert.Null(Send(engine, 0, 1, 1, 0));
        Assert.Null(Send(engine, 0, 3, 0, 0));
        var last = engine.Step();
        Assert.Equal(2, engine.TotalAccepted);
        Assert.Equal(2, engine.TotalRejected);
        Assert.All(last.Rejections, r => Assert.Equal(GhostLapFailure.StaleSequence, r.Code));
        Assert.Single(last.Events, e => e.Kind == MatchEventKinds.BurstManual);
        var hash = MpStateCodec.Hash(engine.State);
        Assert.Equal(GhostLapFailure.NoRunningMatch, Send(engine, 0, 4, 1, 0, clientTick: 2700));
        Assert.Equal(hash, MpStateCodec.Hash(engine.State));
        Assert.Equal(0, engine.QueuedInputs);
    }

    [Fact]
    public async Task Concurrent_producers_race_a_full_final_mailbox_and_every_success_is_disposed()
    {
        var engine = FinalEngine();
        for (var seq = 1; seq <= 16; seq++)
        {
            Assert.Null(Send(engine, 0, seq, 1, 0));
        }
        Assert.Equal(GhostLapFailure.MailboxFull, Send(engine, 0, 17, 1, 0));
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var producers = Enumerable.Range(0, 8).Select(async producer =>
        {
            await go.Task;
            var codes = Enumerable.Range(0, 64)
                .Select(i => Send(engine, producer % 2, 100 + producer * 64 + i, 1, 0, clientTick: 2700)).ToArray();
            Assert.All(codes, code => Assert.True(code is null or GhostLapFailure.NoRunningMatch or GhostLapFailure.MailboxFull));
            return codes.Count(code => code is null);
        }).ToArray();
        go.SetResult();
        var last = engine.Step();
        Assert.True(last.Final);
        Assert.Equal(16 + (await Task.WhenAll(producers)).Sum(), engine.TotalAccepted + engine.TotalRejected);
        Assert.Equal(0, engine.QueuedInputs);
    }

    [Fact]
    public void A_failed_reducer_step_refuses_its_previously_adjudicated_batch_once()
    {
        var engine = FinalEngine(invalidRules: true);
        Assert.Null(Send(engine, 0, 3, 1, 0));
        Assert.Null(Send(engine, 0, 2, 1, 0));
        Assert.Throws<GhostLapTransitionException>(() => engine.Step());

        var refusals = engine.RefuseUndrainedInputs().ToArray();
        Assert.Equal(2, refusals.Length);
        Assert.Contains(refusals, item => item.Seq == 3 && item.Code == GhostLapFailure.NoRunningMatch);
        Assert.Contains(refusals, item => item.Seq == 2 && item.Code == GhostLapFailure.StaleSequence);
        Assert.All(refusals, item => Assert.Equal(-1, item.SequenceFloor));
        Assert.Empty(engine.RefuseUndrainedInputs());
        Assert.Equal(0, engine.TotalAccepted);
        Assert.Equal(2, engine.TotalRejected);
        Assert.Equal(2699, engine.State.Tick);
        Assert.Equal(0, engine.QueuedInputs);
    }

}
