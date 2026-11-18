extern alias realtime;
using System.Reflection;
using GhostLap.Contracts.Solo;
using GhostLap.Simulation;
using GhostLap.Solo;
using GhostLap.Tests.Simulation;
using Marrow.Timeline;
using Microsoft.AspNetCore.SignalR;
using Xunit;
using realtime::GhostLapHost;

namespace Integration.Tests.GhostLap;

public sealed partial class GhostLapSoloCheckpointTests
{
    [Fact]
    public async Task Solo_clock_does_not_accumulate_fractional_millisecond_acceleration()
    {
        var type = typeof(SoloChallengeRegistry).Assembly.GetType("GhostLapHost.SoloTickClock")!;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var clock = Activator.CreateInstance(type)!;
        var next = type.GetMethod("WaitForNextTickAsync")!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        for (var i=0; i<90; i++) await (ValueTask<bool>)next.Invoke(clock, [timeout.Token])!;
        Assert.InRange(watch.Elapsed.TotalMilliseconds, 3000, 6000);
        output.WriteLine($"CLOCK 90 ticks {watch.Elapsed.TotalMilliseconds:F3}ms (33 ms periodic timer would take about 2970ms).");
    }

    [Fact]
    public async Task Deferred_sender_retains_ordered_canonical_pickup_manual_auto_and_terminal_cues()
    {
        // Reuse the frozen transition and existing driver, with no database or real-time game.
        var context = GameContext.Create(42, 1);
        var driver = new CandleSeekingDriver(1);
        var state = GameState.Initial(context);
        var states = new List<GameState>();
        while (!state.IsFinal)
        {
            var inputs = driver.Inputs(state, context);
            state = GhostLapTransition.Instance.Apply(state, state.Tick+1,
                inputs.Select((g,i) => new OrderedInput<GameInput>($"{g.Tick}-{i}", i, g)).ToArray(), context);
            states.Add(state);
        }
        var cues = states.Where(s => s.Events.Length > 0).ToList();
        Assert.Contains(cues, s => s.Events.Any(e => e.Kind == GameEventKind.CandleCollected));
        Assert.Contains(cues, s => s.Events.Any(e => e.Kind == GameEventKind.BurstManual));
        // Existing deterministic deadline fixture, through the real frozen reducer.
        var armed = GameState.Initial(context) with { Tick = 2699 };
        armed = armed with { Players = [armed.Players[0] with { Charge = 4, OverlitDeadlineTick = 2700 }] };
        var terminalAuto = GhostLapTransition.Instance.Apply(armed, 2700, [], context);
        Assert.Contains(terminalAuto.Events, e => e.Kind == GameEventKind.BurstAuto);
        Assert.Contains(terminalAuto.Events, e => e.Kind == GameEventKind.RoundFinished);
        cues = cues.Where(s => s.Tick < 2700).Take(60).Append(terminalAuto).ToList();

        var sender = new DelayedSoloClient();
        var registry = new SoloChallengeRegistry(null!, new SoloHubContext(sender), null!, null!, null!);
        var type = typeof(SoloChallengeRegistry).Assembly.GetType("GhostLapHost.ChallengeSession")!;
        var session = Activator.CreateInstance(type, GuestActor("sender-fixture"), null!, "delayed")!;
        type.GetField("RunStartTimestamp")!.SetValue(session, System.Diagnostics.Stopwatch.GetTimestamp());
        var push = typeof(SoloChallengeRegistry).GetMethod("PushFrame", BindingFlags.NonPublic|BindingFlags.Instance)!;
        var wait = typeof(SoloChallengeRegistry).GetMethod("WaitForFreeSlotAsync", BindingFlags.NonPublic|BindingFlags.Static)!;
        Task Push(GameState s) => (Task)push.Invoke(registry, [session, Tick(s), (double)s.Tick, 0d])!;
        var first = Push(states[0]);
        Assert.False(first.IsCompleted);
        foreach (var cue in cues.Where(s => s.Tick < 2700)) await Push(cue);
        // Round ends while push is held. Production waits for that slot before sending terminal cues.
        var closing = Task.Run(async () => { await (Task)wait.Invoke(null, [session])!; await Push(terminalAuto); });
        await Task.Delay(75);
        Assert.False(closing.IsCompleted);
        sender.Release.TrySetResult();
        await first; await closing;
        Assert.Equal(2, sender.Delivered.Count);
        var last = sender.Delivered[^1];
        Assert.True(last.Final); Assert.False(last.MissedTruncated);
        var observed = (last.Missed ?? []).SelectMany(m => m.Living.Select((e,i) => (m.Tick, Index:i, Event:e)))
            .Concat((last.Living.Events ?? []).Select((e,i) => (last.Tick, Index:i, Event:e))).ToArray();
        var expected = cues.SelectMany(s => ChallengeMapping.Events(s.Events).Select((e,i) => (s.Tick, Index:i, Event:e))).ToArray();
        Assert.Equal(expected, observed);
        Assert.Equal(observed.Length, observed.Select(x => (x.Tick,x.Index)).Distinct().Count());
        Assert.Equal((last.Missed ?? []).Select(m => m.Tick).Order(), (last.Missed ?? []).Select(m => m.Tick));
        Assert.Equal(last.Living.Events, last.Ghost.Events);
        // A subsequent frame drains no duplicate events.
        await Push(states[^1]); Assert.Null(sender.Delivered[^1].Missed);
        // Buffer is bounded at 64 event-bearing ticks and explicitly flags overflow.
        for (var i=0; i<70; i++) type.GetMethod("RecordSkipped")!.Invoke(session, [Tick(cues[0])]);
        var drained = ((MissedTickEvents[] Missed, bool Truncated, int Coalesced))type.GetMethod("DrainSkipped")!.Invoke(session, null)!;
        Assert.Equal(64, drained.Missed.Length); Assert.True(drained.Truncated);
        output.WriteLine($"DEFERRED: {expected.Length} real ordered living cues plus corresponding ghost cues preserved exactly once, including manual/auto Burst and terminal; 64-tick buffer bound and truncation flag verified.");
    }

    private static SoloChallengeTick Tick(GameState state)
    {
        var w = WorldFrame.Of(state);
        var ghost = new SoloTraceFrame(w.Tick,w.XFixed,w.YFixed,w.DistanceFixed,w.Charge,w.IsOverlit,w.OverlitDeadlineTick,
            w.MovementPoints,w.PickupPoints,w.PenaltyPoints,w.TotalScore,w.Pickups,w.Bursts,w.BurstHits,w.HeldMoveX,w.HeldMoveY,
            w.BurstLatch,w.CollectedCount,w.ActiveCandleIds,w.Events,"fixture",default,state.IsFinal);
        return new SoloChallengeTick(state.Tick,w,ghost,0,"fixture","fixture",state.IsFinal);
    }

    private sealed class DelayedSoloClient : IGhostLapSoloClient
    {
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<ChallengeFrameView> Delivered = [];
        private int _calls;
        public async Task ChallengeFrame(ChallengeFrameView frame)
        {
            if (Interlocked.Increment(ref _calls)==1) await Release.Task;
            Delivered.Add(frame);
        }
        public Task ChallengeEnded(SoloRunEnded ended) => Task.CompletedTask;
        public Task Frame(SoloFrame frame) => Task.CompletedTask;
        public Task RunEnded(SoloRunEnded ended) => Task.CompletedTask;
        public Task InputRejected(SoloInputRejected rejected) => Task.CompletedTask;
    }
    private sealed class SoloHubContext(IGhostLapSoloClient client) : IHubContext<GhostLapSoloHub,IGhostLapSoloClient>
    {
        public IHubClients<IGhostLapSoloClient> Clients { get; } = new SoloClients(client);
        public IGroupManager Groups => throw new NotSupportedException();
    }
    private sealed class SoloClients(IGhostLapSoloClient client) : IHubClients<IGhostLapSoloClient>
    {
        public IGhostLapSoloClient Client(string connectionId) => client;
        public IGhostLapSoloClient All => throw new NotSupportedException();
        public IGhostLapSoloClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        public IGhostLapSoloClient Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();
        public IGhostLapSoloClient Group(string groupName) => throw new NotSupportedException();
        public IGhostLapSoloClient GroupExcept(string groupName,IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        public IGhostLapSoloClient Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();
        public IGhostLapSoloClient User(string userId) => throw new NotSupportedException();
        public IGhostLapSoloClient Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
    }
}
