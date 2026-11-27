extern alias realtime;
using GhostLap.Contracts.Multiplayer;
using GhostLap.Contracts.Solo;
using GhostLap.Rules;
using GhostLap.Simulation;
using Marrow.Timeline;
using realtime::GhostLapHost;
using Xunit;

namespace Integration.Tests.GhostLap;

public sealed class GhostLapPresentationTests
{
    [Fact]
    public void Original_projection_carries_committed_input_score_candles_and_ordered_events()
    {
        var context = GameContext.Create(7, 1, [2]);
        var initial = GameState.Initial(context);
        var candle = context.Plan.ActiveAt(1).First();
        var player = initial.Players[0] with { XFixed = candle.Site.X, YFixed = candle.Site.Y, Charge = 3 };
        var before = initial with { Players = [player] };
        var state = GhostLapTransition.Instance.Apply(before, 1,
            [new OrderedInput<GameInput>("move-burst", 0, new GameInput(1, 0, 12, 1, 0, true))], context);
        var frame = SoloMapping.ToOriginalFrame(state, context.Plan);
        var committed = state.Players[0];

        Assert.Equal((state.Tick, committed.XFixed, committed.YFixed, committed.DistanceFixed),
            (frame.Tick, frame.XFixed, frame.YFixed, frame.DistanceFixed));
        Assert.Equal((12, (sbyte)1, (sbyte)0, true), (frame.AckedInputSeq, frame.HeldMoveX, frame.HeldMoveY, frame.BurstLatch));
        Assert.Equal((0, false, 0L, 1), (frame.Charge, frame.Overlit, frame.OverlitDeadlineTick, frame.Bursts));
        Assert.Equal((committed.MovementPoints, committed.PickupPoints, committed.PenaltyPoints, committed.TotalScore),
            (frame.MovementPoints, frame.PickupPoints, frame.PenaltyPoints, frame.TotalScore));
        Assert.Equal(["CandleCollected", "OverlitStarted", "BurstManual"], frame.Events!.Select(e => e.Kind));
        Assert.Equal(candle.Id, frame.Events![0].CandleId);
        Assert.Equal(state.ActiveCandleIds.ToArray(), frame.ActiveCandleIds);
        Assert.DoesNotContain(frame.VisibleCandles!, c => c.Id == candle.Id);
        var expected = context.Plan.Candles
            .Where(c => state.ActiveCandleIds.Contains(c.Id) || c.TelegraphTick <= state.Tick && state.Tick < c.ActivateTick)
            .ToArray();
        Assert.Contains(expected, c => c.ActivateTick > state.Tick);
        Assert.Equal(expected.Select(c => (c.Id, c.Site.X / 1024.0, c.Site.Y / 1024.0,
                state.ActiveCandleIds.Contains(c.Id) ? "active" : "telegraph")),
            frame.VisibleCandles!.Select(c => (c.Id, c.X, c.Y, c.Phase)));
    }

    [Fact]
    public void Original_delivery_bounds_and_orders_events_from_coalesced_ticks()
    {
        var context = GameContext.Create(7, 1, [2]);
        var initial = GameState.Initial(context);
        var type = typeof(SoloRunRegistry).Assembly.GetType("GhostLapHost.OriginalFrameDelivery")!;
        var delivery = Activator.CreateInstance(type)!;
        var push = type.GetMethod("Push")!;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new List<SoloFrame>();
        Task Send(SoloFrame frame)
        {
            sent.Add(frame);
            return sent.Count == 1 ? gate.Task : Task.CompletedTask;
        }

        void Push(long tick, params GameEvent[] events) =>
            push.Invoke(delivery, [initial with { Tick = tick, Events = [.. events] }, context.Plan, (Func<SoloFrame, Task>)Send]);

        Push(1);
        for (var tick = 2; tick <= 67; tick++)
        {
            Push(tick, new GameEvent(GameEventKind.CandleCollected, 0, Value: tick));
        }

        Assert.Single(sent);
        gate.SetResult();
        Push(68, new GameEvent(GameEventKind.BurstManual, 0));
        Push(69);
        Assert.Equal([1L, 2L, 3L], sent.Select(f => f.Seq));
        Assert.True(sent[1].MissedTruncated);
        Assert.Equal(Enumerable.Range(4, 64).Select(t => (long)t), sent[1].Missed!.Select(m => m.Tick));
        Assert.All(sent[1].Missed!, m => Assert.Equal(m.Tick, Assert.Single(m.Events).Value));
        Assert.Equal("BurstManual", Assert.Single(sent[1].Events!).Kind);
        Assert.False(sent[2].MissedTruncated);
        Assert.Empty(sent[2].Missed!);
    }

    [Fact]
    public void Arena_projection_uses_the_frozen_source_and_its_layout_fingerprint()
    {
        var view = GhostLapPresentation.Arena();
        Assert.Equal("arena-v1", view.LayoutId);
        Assert.Equal(global::GhostLap.Multiplayer.MpRulesProfile.LayoutFingerprint, view.LayoutFingerprint);
        Assert.Equal(1600, view.Width); Assert.Equal(900, view.Height); Assert.Equal(9, view.Obstacles.Count);
        Assert.Equal(ArenaV1.Obstacles.Select(r => new ArenaObstacleView(r.X0 / 1024, r.Y0 / 1024, r.X1 / 1024, r.Y1 / 1024)), view.Obstacles);
    }

    [Fact]
    public void Only_active_and_already_telegraphed_candles_are_projected_at_the_snapshot_tick()
    {
        var plan = CandlePlan.Create(7);
        foreach (var tick in new long[] { 0, 30, 90, 300, 2700 })
        {
            // Remove one actual active candle, as if it was collected. It must not reappear from the schedule.
            var active = plan.ActiveIdsAt(tick).Skip(1).ToArray();
            var snapshot = new MatchSnapshot("m", "g", "ghost-mp-v2", "arena-v1", tick, tick, false, [], active);
            var frame = new SnapshotFrame(snapshot, 1, "running", new("spectator", -1, 0, false, -1), new(tick, tick, false, []), false);
            var result = GhostLapPresentation.Project(frame, plan);
            var visible = Assert.IsAssignableFrom<IReadOnlyList<VisibleCandleView>>(result.VisibleCandles);
            var expected = plan.Candles.Where(c => active.Contains(c.Id) || c.TelegraphTick <= tick && tick < c.ActivateTick).ToArray();
            Assert.Equal(expected.Select(c => c.Id), visible.Select(c => c.Id));
            foreach (var c in visible)
            {
                var source = plan.Candles.Single(p => p.Id == c.Id);
                Assert.Equal(source.Site.X / 1024, c.X);
                Assert.Equal(source.Site.Y / 1024, c.Y);
                Assert.Equal(active.Contains(c.Id) ? "active" : "telegraph", c.Phase);
            }
            Assert.Same(snapshot, result.Snapshot);
        }
    }
}
