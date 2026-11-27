using GhostLap.Multiplayer;
using GhostLap.Rules;
using Xunit;
using Xunit.Abstractions;
using static GhostLap.Tests.Multiplayer.MatchTestKit;

namespace GhostLap.Tests.Multiplayer;

/// <summary>
/// Task 10.2 (R10.09): the monotonic 30 Hz scheduler and the match driver, driven on a manual clock so stalls and overruns are exact.
/// The engine, reducer and scheduler are the production classes; only time is simulated.
/// </summary>
public sealed class TickSchedulerTests(ITestOutputHelper output)
{
    private sealed class Run
    {
        public List<long> Ticks { get; } = [];
    }

    private static (MatchStopReason Reason, Run Run, MatchDriver Driver, MatchEngine Engine) Drive(
        int players, Action<long, ManualTime, SwitchableAuthority>? onTick = null, MultiplayerOptions? options = null)
    {
        var time = new ManualTime();
        options ??= new MultiplayerOptions();
        var engine = Engine(players, new MultiplayerOptions { InputsPerSecond = 1000, InputBurst = 1000 }, time);
        var authority = new SwitchableAuthority(Generation);
        var run = new Run();
        var seq = new int[players];
        var driver = new MatchDriver(engine, authority, time, options, outcome =>
        {
            run.Ticks.Add(outcome.Tick);
            // Every slot keeps steering so movement happens on every tick, including catch-up ticks.
            for (var slot = 0; slot < players; slot++)
            {
                Send(engine, slot, ++seq[slot], slot % 2 == 0 ? 1 : -1, (int)((outcome.Tick / 40) % 3) - 1);
            }
            onTick?.Invoke(outcome.Tick, time, authority);
        });
        var reason = driver.RunAsync((delay, _) =>
        {
            time.Advance(delay);
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();
        return (reason, run, driver, engine);
    }

    [Fact]
    public void Due_ticks_follow_the_monotonic_clock_and_one_tick_runs_per_period_when_on_time()
    {
        var options = new MultiplayerOptions();
        var s = new TickScheduler(1_000, 1000, options); // 1 kHz clock starting at 1000
        Assert.Equal(0, s.DueTicks(1_000));
        Assert.Equal(1_034, s.DueAt(1)); // ceil(1000 / 30) = 34 ms after start
        Assert.Equal(1, s.DueTicks(s.DueAt(1)));
        Assert.Equal(GameRulesV1.RoundTicks, s.DueTicks(1_000 + 90_000));
        Assert.Equal(GameRulesV1.RoundTicks, s.DueTicks(1_000 + 900_000)); // capped at the round length
        var d = s.Next(1_010, 0);
        Assert.Equal((0, false), (d.RunNow, d.Overloaded));
        Assert.Equal(TimeSpan.FromMilliseconds(24), d.Wait);
        Assert.Equal(1, s.Next(1_034, 0).RunNow);
    }

    /// <summary>**Validates: Requirements 10.09** A backlog is worked off at most CatchUpTicksPerIteration ticks per iteration; the alarm is time-based.</summary>
    [Fact]
    public void Catch_up_is_bounded_per_iteration_and_overload_needs_sustained_lateness_or_a_huge_backlog()
    {
        var options = new MultiplayerOptions(); // 3 per iteration, alarm > 2 ticks, sustained 2000 ms, max backlog 150
        var s = new TickScheduler(0, 1000, options);
        var stalled = s.Next(700, 0); // 700 ms stall: 21 ticks due
        Assert.Equal((3, 21L, false), (stalled.RunNow, stalled.BacklogTicks, stalled.Overloaded));
        Assert.False(s.Next(2_600, 21).Overloaded);   // late since t=700 for 1900 ms: not yet
        Assert.True(s.Next(2_701, 21).Overloaded);    // late for more than 2000 ms
        var t = new TickScheduler(0, 1000, options);
        Assert.True(t.Next(5_100, 0).Overloaded);     // 153 ticks behind at once
        var u = new TickScheduler(0, 1000, options);
        Assert.False(u.Next(100, 0).Overloaded);      // 3 behind: late
        Assert.False(u.Next(150, 4).Overloaded);      // caught up: the alarm resets
        Assert.False(u.Next(2_500, 72).Overloaded);   // 2 behind is within the alarm
    }

    /// <summary>
    /// **Validates: Requirements 10.09** A 700 ms scheduler stall: every logical tick still runs, in order, none skipped; movement per tick
    /// never exceeds one fixed step (no elapsed-time scaling, no catch-up distance); the backlog is repaired and the match finishes.
    /// </summary>
    [Fact]
    public void A_700_ms_stall_is_repaired_by_bounded_catch_up_without_skipping_ticks_or_scaling_movement()
    {
        var (reason, run, driver, engine) = Drive(2, (tick, time, _) =>
        {
            if (tick == 300)
            {
                time.Advance(TimeSpan.FromMilliseconds(700));
            }
        });
        Assert.Equal(MatchStopReason.Finished, reason);
        Assert.Equal(Enumerable.Range(1, GameRulesV1.RoundTicks).Select(i => (long)i), run.Ticks);
        var telemetry = driver.Scheduler!.Telemetry;
        output.WriteLine($"max backlog {telemetry.MaxBacklogTicks} ticks, catch-up iterations {telemetry.CatchUpIterations}, late ticks {telemetry.LateTicks}, max start lateness {telemetry.MaxStartLatenessMicroseconds} us");
        Assert.InRange(telemetry.MaxBacklogTicks, 21, 22);
        Assert.InRange(telemetry.CatchUpIterations, 7, 8);
        Assert.True(engine.State.IsFinal);
        // Total distance is bounded by one cardinal step per executed tick: nothing was awarded for the stalled wall time.
        Assert.All(engine.State.World.Players, p => Assert.True(p.DistanceFixed <= GameRulesV1.RoundTicks * (long)GameRulesV1.CardinalStepFixed));
    }

    /// <summary>**Validates: Requirements 10.09** Every tick costing 50 ms (more than the 33.3 ms period) ends in Overloaded, not a 90 s match over unbounded wall time.</summary>
    [Fact]
    public void A_persistent_overrun_ends_in_overload_instead_of_an_endlessly_late_match()
    {
        var (reason, run, driver, _) = Drive(4, (_, time, _) => time.Advance(TimeSpan.FromMilliseconds(50)));
        output.WriteLine($"overrun: stopped after {run.Ticks.Count} ticks, max backlog {driver.Scheduler!.Telemetry.MaxBacklogTicks}");
        Assert.Equal(MatchStopReason.Overloaded, reason);
        // Backlog after n ticks is about n/2: above the 2-tick alarm from tick 6 (300 ms), overloaded 2000 ms later (about tick 46).
        Assert.InRange(run.Ticks.Count, 40, 60);
        Assert.Equal(Enumerable.Range(1, run.Ticks.Count).Select(i => (long)i), run.Ticks);
    }

    [Fact]
    public void A_stall_longer_than_the_maximum_backlog_aborts_at_once()
    {
        var (reason, run, _, _) = Drive(2, (tick, time, _) =>
        {
            if (tick == 10)
            {
                time.Advance(TimeSpan.FromSeconds(6));
            }
        });
        Assert.Equal(MatchStopReason.Overloaded, reason);
        Assert.Equal(10, run.Ticks.Count);
    }

    /// <summary>**Validates: Requirements 10.05** Losing authority stops the loop before the next tick: nothing is executed or published afterwards.</summary>
    [Fact]
    public void Losing_authority_stops_the_loop_before_the_next_tick()
    {
        var (reason, run, _, engine) = Drive(3, (tick, _, authority) =>
        {
            if (tick == 50)
            {
                authority.Lose();
            }
        });
        Assert.Equal(MatchStopReason.AuthorityLost, reason);
        Assert.Equal(50, run.Ticks.Max());
        Assert.Equal(50, engine.State.Tick);
    }

    [Fact]
    public async Task Cancellation_stops_the_loop()
    {
        var time = new ManualTime();
        var engine = Engine(2, time: time);
        using var cts = new CancellationTokenSource();
        var published = 0;
        var driver = new MatchDriver(engine, new SwitchableAuthority(Generation), time, new MultiplayerOptions(), _ =>
        {
            if (++published == 20)
            {
                cts.Cancel();
            }
        });
        var reason = await driver.RunAsync((delay, _) =>
        {
            time.Advance(delay);
            return Task.CompletedTask;
        }, cts.Token);
        Assert.Equal(MatchStopReason.Cancelled, reason);
        Assert.Equal(20, engine.State.Tick);
    }
}
