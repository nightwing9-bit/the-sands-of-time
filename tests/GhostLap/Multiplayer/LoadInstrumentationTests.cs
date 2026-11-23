using GhostLap.Multiplayer;
using Npgsql;
using Xunit;
using static GhostLap.Tests.Multiplayer.MatchTestKit;

namespace GhostLap.Tests.Multiplayer;

/// <summary>
/// Task 10.5 (prepared, Codex verification pending): the instrumentation records what it claims to record, and the admission limits
/// refuse a start before anything durable exists. These are short smoke tests of the mechanism; they measure nothing about target hardware.
/// </summary>
public sealed class LoadInstrumentationTests
{
    [Fact]
    public void A_series_reports_nearest_rank_percentiles_and_keeps_the_all_time_maximum_when_the_window_wraps()
    {
        var series = new SampleSeries(100);
        for (var i = 1; i <= 100; i++)
        {
            series.Add(i);
        }
        Assert.Equal(new SeriesSummary(100, 100, 50, 95, 99, 100), series.Summarize());

        var wrapped = new SampleSeries(10);
        wrapped.Add(9_999); // falls out of the window, stays the maximum
        for (var i = 1; i <= 24; i++)
        {
            wrapped.Add(i);
        }
        var s = wrapped.Summarize();
        Assert.Equal((25L, 10L, 9_999L), (s.Count, s.Window, s.Max));
        Assert.Equal(19, s.P50); // the window holds 15..24: the 5th of 10 sorted samples
        Assert.Equal(default, new SampleSeries().Summarize());
    }

    [Fact]
    public async Task The_driver_records_one_sample_of_every_series_per_executed_tick_and_nothing_else_changes()
    {
        const int ticks = 90;
        var time = new ManualTime();
        var options = new MultiplayerOptions { InputsPerSecond = 1000, InputBurst = 1000 };
        var engine = Engine(2, options, time);
        var telemetry = new MatchTelemetry();
        using var cts = new CancellationTokenSource();
        var driver = new MatchDriver(engine, new SwitchableAuthority(Generation), time, options, outcome =>
        {
            if (outcome.Tick == ticks)
            {
                cts.Cancel();
            }
        }, telemetry);
        var reason = await driver.RunAsync((delay, _) =>
        {
            time.Advance(delay);
            return Task.CompletedTask;
        }, cts.Token);

        Assert.Equal(MatchStopReason.Cancelled, reason);
        Assert.Equal(ticks, engine.State.Tick);
        Assert.Equal(ticks, telemetry.Ticks);
        foreach (var series in new[] { telemetry.TickExecutionMicros, telemetry.StartLatenessMicros, telemetry.BacklogTicks, telemetry.MailboxDepth })
        {
            Assert.Equal(ticks, series.Summarize().Count);
        }
        // The manual clock waits exactly until each tick is due: no lateness, no backlog.
        Assert.Equal(0, telemetry.StartLatenessMicros.Summarize().Max);
        Assert.Equal(0, telemetry.BacklogTicks.Summarize().Max);

        // The same run without telemetry reaches the same state: instrumentation observes, it does not participate.
        var plainTime = new ManualTime();
        var plain = Engine(2, options, plainTime);
        using var plainCts = new CancellationTokenSource();
        await new MatchDriver(plain, new SwitchableAuthority(Generation), plainTime, options, o =>
        {
            if (o.Tick == ticks)
            {
                plainCts.Cancel();
            }
        }).RunAsync((delay, _) =>
        {
            plainTime.Advance(delay);
            return Task.CompletedTask;
        }, plainCts.Token);
        Assert.Equal(MpStateCodec.Hash(plain.State), MpStateCodec.Hash(engine.State));
    }

    [Fact]
    public async Task A_catch_up_backlog_is_visible_in_the_backlog_and_lateness_series()
    {
        var time = new ManualTime();
        var options = new MultiplayerOptions { InputsPerSecond = 1000, InputBurst = 1000 };
        var engine = Engine(2, options, time);
        var telemetry = new MatchTelemetry();
        using var cts = new CancellationTokenSource();
        var stalled = false;
        var driver = new MatchDriver(engine, new SwitchableAuthority(Generation), time, options, outcome =>
        {
            if (outcome.Tick == 30 && !stalled)
            {
                stalled = true;
                time.Advance(TimeSpan.FromMilliseconds(400)); // the scheduler thread was away
            }
            if (outcome.Tick == 120)
            {
                cts.Cancel();
            }
        }, telemetry);
        await driver.RunAsync((delay, _) =>
        {
            time.Advance(delay);
            return Task.CompletedTask;
        }, cts.Token);

        Assert.True(telemetry.BacklogTicks.Summarize().Max >= 10, "a 400 ms stall leaves a visible backlog");
        Assert.True(telemetry.StartLatenessMicros.Summarize().Max >= 100_000);
        Assert.Equal(120, telemetry.Ticks); // every tick still ran, in order
    }

    [Fact]
    public void Snapshot_size_is_measured_on_the_recipient_sender_only_when_enabled()
    {
        var measured = SnapshotBytesAfterOneSend(measure: true);
        Assert.True(measured.Count > 0 && measured.Max > 0 && measured.Max == measured.P99);
        Assert.Equal(0, SnapshotBytesAfterOneSend(measure: false).Count);
    }

    private static SeriesSummary SnapshotBytesAfterOneSend(bool measure)
    {
        var options = new MultiplayerOptions { MeasureSnapshotBytes = measure, SnapshotIntervalTicks = 1 };
        var config = Seal(2);
        var authority = new SwitchableAuthority(Generation);
        var runtime = MatchRuntime.Create(config, options, new ManualTime(), authority, new NullMatchTransport(), TimeProvider.System);
        runtime.Fanout.Attach(new MatchViewer("c0", config.Slots[0].MemberId, false, 0, 1, true));
        runtime.PublishTick(runtime.Engine.Step());
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && runtime.Fanout.Find("c0") is { Stats.SnapshotsSent: < 1 })
        {
            Thread.Sleep(10);
        }
        Thread.Sleep(50); // the size is recorded right after the send counter
        runtime.Fanout.Stop();
        return runtime.Telemetry.SnapshotBytes.Summarize();
    }

    // ------------------------------------------------------------------------------------------ admission limits

    private static MatchRegistry Registry(MultiplayerOptions options, out NpgsqlDataSource source)
    {
        source = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1;Pooling=false");
        return new MatchRegistry(options, new ManualTime(), new SwitchableAuthority(Generation), new MatchAuthorityStore(source));
    }

    [Fact]
    public async Task A_start_beyond_the_concurrent_match_limit_is_refused_before_any_receipt_or_runtime_exists()
    {
        var registry = Registry(new MultiplayerOptions { MaxConcurrentMatches = 0 }, out var source); // the validator forbids 0; the gate itself must hold
        await using var _ = source;
        var config = Seal(4);
        Assert.Equal(StartAdmissionOutcome.Unavailable, await registry.AdmitAsync(config, CancellationToken.None));
        Assert.Equal(1, registry.StartsDeniedForCapacity);
        Assert.Equal(0, registry.ReservedStarts);
        Assert.Empty(registry.Matches);
        Assert.Null(registry.StatusOf(config.MatchId));
        var load = registry.CaptureLoad();
        Assert.Equal((0, 0, 0, 1L), (load.Capacity.LiveMatches, load.Capacity.ReservedStarts, load.Capacity.MaxConcurrentMatches, load.Capacity.StartsDeniedForCapacity));
        Assert.Empty(load.Matches);
    }

    [Fact]
    public async Task A_failed_receipt_commit_releases_its_reservation_and_is_not_counted_as_a_capacity_denial()
    {
        var registry = Registry(new MultiplayerOptions { MaxConcurrentMatches = 1 }, out var source);
        await using var _ = source;
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(StartAdmissionOutcome.Unavailable, await registry.AdmitAsync(Seal(2, secret: (byte)(i + 1)), CancellationToken.None));
        }
        Assert.Equal(0, registry.ReservedStarts); // three failed commits did not leak the single slot
        Assert.Equal(0, registry.StartsDeniedForCapacity);
    }

    [Fact]
    public void The_new_limits_are_range_checked_at_startup_and_the_defaults_cover_the_intended_envelope()
    {
        var defaults = new MultiplayerOptions();
        Assert.Empty(MultiplayerStartup.Validate(defaults));
        Assert.True(defaults.MaxConcurrentMatches >= 4, "at least four concurrent 4-player rooms");
        Assert.True(defaults.MaxTotalConnections >= 4 * (4 + 2), "four rooms with two spectators each");
        Assert.False(defaults.MeasureSnapshotBytes);
        Assert.Equal("", defaults.ProfileOutputPath);

        Assert.Contains(MultiplayerStartup.Validate(new MultiplayerOptions { MaxConcurrentMatches = 0 }), e => e.Contains(nameof(MultiplayerOptions.MaxConcurrentMatches)));
        Assert.Contains(MultiplayerStartup.Validate(new MultiplayerOptions { MaxConcurrentMatches = 1025 }), e => e.Contains(nameof(MultiplayerOptions.MaxConcurrentMatches)));
        Assert.Contains(MultiplayerStartup.Validate(new MultiplayerOptions { MaxTotalConnections = 0 }), e => e.Contains(nameof(MultiplayerOptions.MaxTotalConnections)));
        Assert.Contains(
            MultiplayerStartup.Validate(new MultiplayerOptions { MaxTotalConnections = 3, MaxConnectionsPerPrincipal = 6 }),
            e => e.Contains(nameof(MultiplayerOptions.MaxTotalConnections)));
        Assert.Contains(MultiplayerStartup.Validate(new MultiplayerOptions { ProfileIntervalSeconds = 0 }), e => e.Contains(nameof(MultiplayerOptions.ProfileIntervalSeconds)));
        Assert.Contains(MultiplayerStartup.Validate(new MultiplayerOptions { ProfileOutputPath = "   " }), e => e.Contains(nameof(MultiplayerOptions.ProfileOutputPath)));
        Assert.Empty(MultiplayerStartup.Validate(new MultiplayerOptions { ProfileOutputPath = "/tmp/ghostlap-profile.jsonl" }));
    }
}
