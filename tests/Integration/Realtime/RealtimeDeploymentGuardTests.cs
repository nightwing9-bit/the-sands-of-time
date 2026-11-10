using System.Diagnostics;
using Integration.Tests.Support;
using Marrow.Infrastructure.Realtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using Xunit;

namespace Integration.Tests.Realtime;

/// <summary>
/// R03.07 runtime half: the real <c>Hosts.Realtime</c> composition takes the single-instance room-authority lock in the
/// real PostgreSQL database, a second instance refuses to start, and a lost lock stops the instance. R03.06: transport
/// bounds cannot be configured out of their fixed ranges.
/// </summary>
[Collection(IdentityCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RealtimeInstanceGuardTests(IdentityDatabase db)
{
    private RealtimeHostFactory NewHost(Dictionary<string, string?>? settings = null) =>
        new(db, IdentityApiFactory.NewMasterSecret(), settings);

    private Task<long> LockSessionsAsync() => db.ScalarAsync<long>(
        "SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a ON a.pid = l.pid "
        + "WHERE l.locktype = 'advisory' AND l.granted AND a.application_name = @name",
        ("name", RealtimeInstanceGuard.ApplicationName));

    private static Exception StartFailure(RealtimeHostFactory host)
    {
        var thrown = Assert.ThrowsAny<Exception>(() => host.Server);
        return thrown;
    }

    private static T? Find<T>(Exception e) where T : Exception
    {
        for (Exception? current = e; current is not null; current = current.InnerException)
        {
            if (current is T match)
            {
                return match;
            }
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    if (Find<T>(inner) is { } found)
                    {
                        return found;
                    }
                }
            }
        }
        return null;
    }

    private static async Task DisposeQuietlyAsync(RealtimeHostFactory host)
    {
        try
        {
            await host.DisposeAsync();
        }
        catch (Exception)
        {
            // A host whose start failed may throw again on dispose; the assertion already happened.
        }
    }

    [Fact]
    public async Task Second_Realtime_instance_on_the_same_database_refuses_to_start_until_the_first_stops()
    {
        await using var first = NewHost();
        _ = first.Server;
        Assert.True(first.Services.GetRequiredService<RealtimeInstanceGuard>().IsHeld);
        Assert.Equal(1, await LockSessionsAsync());
        Assert.Contains(first.Logs.Lines, l => l.EventId == 3207);

        var second = NewHost(new() { ["Realtime:InstanceLockAcquireSeconds"] = "1" });
        // Program catches the refusal and exits with RealtimeInstanceRefusedException.ExitCode, so the test host only
        // sees that start failed; the refusal itself is asserted from the host's own logs.
        StartFailure(second);
        Assert.Contains(second.Logs.Lines, l => l.EventId == 3208 && l.Message.Contains("held-by-another-instance"));
        var refusal = Assert.Single(second.Logs.Lines, l => l.Exception?.Contains(nameof(RealtimeInstanceRefusedException)) == true);
        Assert.Contains("another Realtime instance already holds the room-authority lock", refusal.Exception);
        Assert.Contains("Exactly one Realtime replica", refusal.Exception);
        Assert.DoesNotContain(db.AppConnectionString, second.Logs.AllText());
        await DisposeQuietlyAsync(second);

        // The first instance is unaffected and still the only holder.
        var live = await first.CreateClient().GetAsync("/health/live");
        Assert.True(live.IsSuccessStatusCode);
        Assert.Equal(1, await LockSessionsAsync());

        await first.DisposeAsync();
        Assert.Equal(0, await LockSessionsAsync());

        await using var third = NewHost(new() { ["Realtime:InstanceLockAcquireSeconds"] = "5" });
        _ = third.Server;
        Assert.True(third.Services.GetRequiredService<RealtimeInstanceGuard>().IsHeld);
        Assert.Equal(1, await LockSessionsAsync());
    }

    [Fact]
    public async Task Lost_lock_connection_stops_the_instance_with_a_failing_exit_code()
    {
        var secret = IdentityApiFactory.NewMasterSecret();
        await using var api = new IdentityApiFactory(db, secret);
        await using var host = new RealtimeHostFactory(db, secret, new() { ["Realtime:InstanceLockCheckSeconds"] = "1" });
        _ = host.Server;
        var guard = host.Services.GetRequiredService<RealtimeInstanceGuard>();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        Assert.True(guard.IsHeld);
        Assert.Equal(0, guard.ExitCode);

        // A live client of the instance that is about to lose authority.
        var browser = api.NewBrowser();
        await browser.PostAsync("/auth/guest/start");
        await using var client = host.Connect(guestCookie: browser.Cookies[Flows.GuestCookie]);
        var clientClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Closed += _ => { clientClosed.TrySetResult(); return Task.CompletedTask; };
        await client.StartAsync();

        var terminated = await db.ScalarAsync<long>(
            "SELECT count(*) FROM (SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name = @name) t",
            ("name", RealtimeInstanceGuard.ApplicationName));
        Assert.Equal(1, terminated);

        await Eventually.TrueAsync(() => lifetime.ApplicationStopping.IsCancellationRequested, "host stops after losing the lock");
        Assert.True(guard.AuthorityLost);
        Assert.False(guard.IsHeld);
        Assert.Equal(1, guard.ExitCode);
        Assert.Contains(host.Logs.Lines, l => l.EventId == 3209);
        // Fail closed: existing clients are dropped immediately, not after graceful shutdown.
        await clientClosed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, await LockSessionsAsync());
    }

    [Theory]
    [InlineData("MaxInboundMessageBytes", "0")]
    [InlineData("MaxInboundMessageBytes", "2097152")]
    [InlineData("MaxPayloadBytes", "100")]
    [InlineData("InvocationsPerSecond", "0")]
    [InlineData("InvocationBurst", "0")]
    [InlineData("MaxSubscriptionsPerConnection", "100000")]
    [InlineData("InstanceLockCheckSeconds", "0")]
    [InlineData("AccessRevalidationSeconds", "0")]
    [InlineData("AccessRevalidationSeconds", "3600")]
    public async Task Out_of_range_realtime_limits_fail_startup_before_the_lock_is_taken(string key, string value)
    {
        var host = NewHost(new() { ["Realtime:" + key] = value });
        var failure = Find<OptionsValidationException>(StartFailure(host));
        Assert.NotNull(failure);
        Assert.Contains("Realtime:" + key, failure.Message);
        Assert.Equal(0, await LockSessionsAsync());
        await DisposeQuietlyAsync(host);
    }
}

/// <summary>R03.07 deployment-config half: <c>scripts/checks/realtime-scale-guard.sh</c> on the real resolved Compose model.</summary>
[Trait("Category", "Integration")]
public sealed class RealtimeScaleGuardScriptTests
{
    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TheSandsOfTime.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException("Repository root not found.");
    }

    private static async Task<(int ExitCode, string Output)> RunGuardAsync(string? overrideYaml)
    {
        var root = RepositoryRoot();
        var start = new ProcessStartInfo("bash") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "scripts", "checks", "realtime-scale-guard.sh"));
        string? overrideFile = null;
        if (overrideYaml is not null)
        {
            overrideFile = Path.Combine(Path.GetTempPath(), $"realtime-guard-{Guid.NewGuid():N}.yml");
            await File.WriteAllTextAsync(overrideFile, overrideYaml);
            start.ArgumentList.Add("-f");
            start.ArgumentList.Add("deploy/compose/docker-compose.yml");
            start.ArgumentList.Add("-f");
            start.ArgumentList.Add(overrideFile);
        }
        try
        {
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // Never leave a hung bash/docker child behind a failed test.
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("realtime-scale-guard.sh did not exit within 90 s");
            }
            return (process.ExitCode, await stdout + await stderr);
        }
        finally
        {
            if (overrideFile is not null)
            {
                File.Delete(overrideFile);
            }
        }
    }

    [Fact]
    public async Task Repository_compose_file_passes_with_exactly_one_realtime_replica()
    {
        var (exit, output) = await RunGuardAsync(null);
        Assert.True(exit == 0, output);
        Assert.Contains("[PASSED] realtime-scale-guard", output);
    }

    [Fact]
    public async Task Explicit_single_replica_override_passes()
    {
        var (exit, output) = await RunGuardAsync("services:\n  realtime:\n    deploy:\n      replicas: 1\n");
        Assert.True(exit == 0, output);
    }

    [Theory]
    [InlineData("services:\n  realtime:\n    deploy:\n      replicas: 2\n", "services.realtime.deploy.replicas=2")]
    [InlineData("services:\n  realtime:\n    scale: 3\n    deploy: !reset {}\n", "services.realtime.scale=3")]
    [InlineData("services:\n  realtime:\n    deploy:\n      mode: global\n      replicas: !reset null\n", "services.realtime.deploy.mode=global")]
    [InlineData("services:\n  realtime-b:\n    build:\n      context: ../..\n      dockerfile: deploy/docker/Realtime.Dockerfile\n", "more than one Realtime service: realtime, realtime-b")]
    public async Task Configuration_that_runs_more_than_one_realtime_process_fails_the_guard(string overrideYaml, string expected)
    {
        var (exit, output) = await RunGuardAsync(overrideYaml);
        Assert.True(exit == 1, output);
        Assert.Contains("Realtime must run as exactly ONE replica", output);
        Assert.Contains(expected, output);
    }
}
