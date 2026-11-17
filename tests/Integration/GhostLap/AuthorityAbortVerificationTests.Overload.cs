extern alias realtime;
using GhostLap.Contracts.Multiplayer;
using GhostLap.Multiplayer;
using Integration.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using realtime::GhostLapHost;
using Xunit;

namespace Integration.Tests.GhostLap;

public sealed partial class AuthorityAbortVerificationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sustained_overload_stops_gameplay_and_reports_the_database_outcome(bool loseAbortConnections)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        var secret = IdentityApiFactory.NewMasterSecret();
        SealedRoundConfig config;
        string? terminalSnapshot;
        await using (var host = new RealtimeHostFactory(db, secret))
        {
            var authority = host.Services.GetRequiredService<GhostLapRealtimeAuthority>();
            var store = host.Services.GetRequiredService<MatchAuthorityStore>();
            var options = new MultiplayerOptions { AbortAttempts = 2 };
            var roomTime = new ManualClock();
            var tickTime = new OverloadClock();
            var registry = new MatchRegistry(options, tickTime, authority, store, tickTime.WaitAsync);
            var owner = new ActiveRoomOwner(options, roomTime, registry, registry, new CryptoRoomRandom(), authority.GenerationId, registry);
            try
            {
                var (caller, joined, round) = BeginCountdown(owner);
                var matchId = Guid.Parse(round.MatchId);
                await using (var startGate = await PauseAtAsync(
                    "room_start", "AFTER INSERT ON ghostlap.match_session", $"NEW.match_id = '{matchId:D}'", 103, ct))
                {
                    roomTime.Advance(TimeSpan.FromMilliseconds(options.CountdownMilliseconds + 1));
                    var sweep = owner.SweepAsync(ct).AsTask();
                    await WaitBlockedAsync(GhostLapMatchDataSource.ApplicationName, startGate.BackendPid, ct);
                    var beforeCommit = Value(owner.GetRoom(caller, joined.View.RoomId));
                    Assert.Equal(RoomPhases.Countdown, beforeCommit.Phase);
                    Assert.Null(beforeCommit.MatchStatus);
                    Assert.Null(registry.Find(matchId));
                    Assert.Null(await ReceiptSnapshotAsync(matchId));
                    await startGate.ReleaseAsync(ct);
                    await sweep;
                }

                var running = Value(owner.GetRoom(caller, joined.View.RoomId));
                Assert.Equal((RoomPhases.StartAdmitted, MatchStatuses.Running), (running.Phase, running.MatchStatus));
                var runtime = Assert.IsType<MatchRuntime>(registry.Find(matchId));
                await tickTime.FirstWait.Task.WaitAsync(ct);
                config = runtime.Config;
                await AssertReceiptAsync(config, "absent", "Started:0:-");
                Assert.True(runtime.Activated);
                Assert.Equal(0, runtime.Engine.State.Tick);

                if (loseAbortConnections)
                {
                    // Terminate just the backends that attempt this match's overload abort; keep PostgreSQL and the guard running.
                    await ExecuteAsync($"""
                        CREATE FUNCTION ghostlap.verify_lose_abort_connection() RETURNS trigger LANGUAGE plpgsql AS $$
                        BEGIN
                            PERFORM pg_terminate_backend(pg_backend_pid());
                            RAISE EXCEPTION USING ERRCODE = '08006', MESSAGE = 'verification: abort connection lost';
                        END $$;
                        CREATE TRIGGER zz_verify_lose_abort_connection BEFORE UPDATE ON ghostlap.match_session
                        FOR EACH ROW WHEN (NEW.match_id = '{matchId:D}' AND NEW.abort_reason = 'overload')
                        EXECUTE FUNCTION ghostlap.verify_lose_abort_connection();
                        """, ct);
                }

                try
                {
                    await using var abortGate = await PauseAtAsync(
                        "room_abort", "BEFORE UPDATE ON ghostlap.match_session",
                        $"NEW.match_id = '{matchId:D}' AND NEW.abort_reason = 'overload'", 104, ct);
                    GameplayInput Input(int seq, long tick) => new(round.MatchId, joined.ControllerEpoch, tick, seq, 1, 0, false);
                    Assert.Null(owner.AdmitInput(caller.ConnectionId, caller.Subject, Input(1, 1)));
                    tickTime.Overload(runtime);
                    var abortBackend = await WaitBlockedAsync(GhostLapMatchDataSource.ApplicationName, abortGate.BackendPid, ct);
                    Assert.Equal(MatchStatuses.Aborting, runtime.Status);
                    Assert.Equal(MatchStopReason.Overloaded, runtime.StopReason);
                    var tick = runtime.Engine.State.Tick;
                    var backlog = runtime.Driver!.Scheduler!.Telemetry.MaxBacklogTicks;
                    Assert.InRange(backlog, options.LateAlarmTicks + 1, options.OverloadMaxBacklogTicks);
                    Assert.InRange(tick, 1, config.TargetTicks - 1);
                    Assert.False(runtime.Engine.State.IsFinal);
                    Assert.Equal(1, runtime.Engine.State.World.Players[runtime.Engine.SlotOf(joined.MemberId)!.Value].LastSequence);
                    Assert.Equal(tick, Interlocked.Read(ref runtime.PublishedTicks));
                    var frozen = runtime.Engine.State;
                    Assert.Equal(GhostLapFailure.NoRunningMatch, owner.AdmitInput(caller.ConnectionId, caller.Subject, Input(2, tick + 1)));
                    await AssertReceiptAsync(config, "Started", "Started:0:-");

                    // An independent room admits and advances while the first room is blocked on PostgreSQL.
                    var (otherCaller, otherJoined, otherRound) = BeginCountdown(owner);
                    roomTime.Advance(TimeSpan.FromMilliseconds(options.CountdownMilliseconds + 1));
                    await owner.SweepAsync(ct);
                    Assert.Equal(MatchStatuses.Running, Value(owner.GetRoom(otherCaller, otherJoined.View.RoomId)).MatchStatus);
                    var other = Assert.IsType<MatchRuntime>(registry.Find(Guid.Parse(otherRound.MatchId)));
                    await UntilAsync(() => Task.FromResult(Interlocked.Read(ref other.PublishedTicks) >= 3),
                        $"Unrelated match {other.Config.MatchId:D} did not advance while {matchId:D} waited for PostgreSQL", ct);
                    Assert.Equal(MatchStatuses.Aborting, runtime.Status);
                    await AssertReceiptAsync(other.Config, "absent", "Started:0:-");
                    Assert.Same(frozen, runtime.Engine.State);

                    await abortGate.ReleaseAsync(ct);
                    await UntilAsync(() => Task.FromResult(runtime.Status is MatchStatuses.Aborted or MatchStatuses.Unverified),
                        $"Generation {authority.GenerationId:D}, match {matchId:D}: abort did not settle, last status {runtime.Status}", ct);
                    await owner.SweepAsync(ct);
                    var expectedStatus = loseAbortConnections ? MatchStatuses.Unverified : MatchStatuses.Aborted;
                    Assert.Equal(expectedStatus, runtime.Status);
                    Assert.Equal(expectedStatus, Value(owner.GetRoom(caller, joined.View.RoomId)).MatchStatus);
                    if (loseAbortConnections)
                    {
                        Assert.False(await db.ScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE pid = @pid)",
                            ("pid", abortBackend)), $"The abort connection {abortBackend} was expected to terminate.");
                        Assert.True(authority.IsValid);
                    }
                    await AssertReceiptAsync(config, "Started", loseAbortConnections ? "Started:0:-" : "Aborted:1:overload");
                    Assert.Equal(GhostLapFailure.NoRunningMatch, owner.AdmitInput(caller.ConnectionId, caller.Subject, Input(3, tick + 1)));
                    Assert.Same(frozen, runtime.Engine.State);
                    Assert.Equal(tick, Interlocked.Read(ref runtime.PublishedTicks));

                    terminalSnapshot = await ReceiptSnapshotAsync(matchId);
                    if (!loseAbortConnections)
                    {
                        var duplicate = await store.AbortAsync(matchId, authority.GenerationId, AbortReasons.HostStopping, ct);
                        Assert.Equal((AbortOutcome.AlreadyTerminal, AbortReasons.Overload), (duplicate.Outcome, duplicate.Receipt?.AbortReason));
                        Assert.Equal(terminalSnapshot, await ReceiptSnapshotAsync(matchId));
                    }
                    await AssertNoResultStorageAsync();
                }
                finally
                {
                    if (loseAbortConnections)
                    {
                        await ExecuteAsync("""
                            DROP TRIGGER zz_verify_lose_abort_connection ON ghostlap.match_session;
                            DROP FUNCTION ghostlap.verify_lose_abort_connection();
                            """, CancellationToken.None);
                    }
                }
            }
            finally
            {
                // Let any parked scheduler leave its first wait even if an earlier assertion failed.
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                await registry.StopAllAsync(stop.Token);
            }
        }

        await using var restarted = new RealtimeHostFactory(db, secret);
        var successor = restarted.Services.GetRequiredService<GhostLapRealtimeAuthority>();
        var restartedRegistry = restarted.Services.GetRequiredService<MatchRegistry>();
        Assert.True(successor.IsValid);
        Assert.Empty(restartedRegistry.Matches);
        await AssertReceiptAsync(config, loseAbortConnections ? "Started" : "Aborted",
            loseAbortConnections ? "Aborted:1:generation_retired" : "Aborted:1:overload");
        if (!loseAbortConnections)
        {
            Assert.Equal(terminalSnapshot, await ReceiptSnapshotAsync(config.MatchId));
        }
        Assert.NotEqual(StartAdmissionOutcome.Admitted,
            await restartedRegistry.AdmitAsync(config with { AuthorityGeneration = successor.GenerationId }, ct));
        Assert.Null(restartedRegistry.Find(config.MatchId));
    }

    [Fact]
    public async Task Committed_receipt_that_never_activates_is_aborted_by_the_registry_sweep()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        await using var host = new RealtimeHostFactory(db, IdentityApiFactory.NewMasterSecret());
        var authority = host.Services.GetRequiredService<GhostLapRealtimeAuthority>();
        var store = host.Services.GetRequiredService<MatchAuthorityStore>();
        var clock = new ManualClock();
        var options = new MultiplayerOptions();
        var registry = new MatchRegistry(options, clock, authority, store);
        var config = Seal(authority.GenerationId);
        Assert.Equal(StartAdmissionOutcome.Admitted, await registry.AdmitAsync(config, ct));
        var runtime = Assert.IsType<MatchRuntime>(registry.Find(config.MatchId));
        await AssertReceiptAsync(config, "absent", "Started:0:-");
        clock.Advance(TimeSpan.FromMilliseconds(options.ActivationTimeoutMilliseconds + 1));
        registry.Sweep();
        await UntilAsync(() => Task.FromResult(runtime.Status is MatchStatuses.Aborted or MatchStatuses.Unverified),
            $"Generation {authority.GenerationId:D}, match {config.MatchId:D}: unactivated receipt abort did not settle", ct);
        Assert.Equal(MatchStatuses.Aborted, runtime.Status);
        await AssertReceiptAsync(config, "Started", "Aborted:1:not_activated");
        registry.Activate(config.MatchId, []);
        Assert.False(runtime.Activated);
        Assert.Null(runtime.Driver);
        Assert.Equal(StartAdmissionOutcome.Closed, await registry.AdmitAsync(config, ct));
    }

    private static (RoomCaller Caller, JoinedRoom Joined, SealedRoundView Round) BeginCountdown(ActiveRoomOwner owner)
    {
        var host = new RoomCaller("guest:" + Guid.NewGuid(), Guid.NewGuid().ToString("N"));
        var peer = new RoomCaller("guest:" + Guid.NewGuid(), Guid.NewGuid().ToString("N"));
        var joined = Value(owner.CreateRoom(host, new("create")));
        Value(owner.JoinRoom(peer, new("join", joined.RoomCode, false)));
        Value(owner.SetReady(host, new("ready", joined.View.RoomId, true)));
        Value(owner.SetReady(peer, new("ready", joined.View.RoomId, true)));
        var countdown = Value(owner.StartCountdown(host, new("start", joined.View.RoomId))).View;
        Assert.Equal(RoomPhases.Countdown, countdown.Phase);
        return (host, joined, Assert.IsType<SealedRoundView>(countdown.Round));
    }

    private static T Value<T>(OwnerResult<T> result) where T : class
    {
        Assert.True(result.Ok, result.Code);
        return Assert.IsType<T>(result.Value);
    }

    private class ManualClock : TimeProvider
    {
        private long _milliseconds;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => Interlocked.Read(ref _milliseconds);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _milliseconds, (long)Math.Ceiling(by.TotalMilliseconds));
    }

    private sealed class OverloadClock : ManualClock
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private MatchRuntime? _overloaded;

        public TaskCompletionSource FirstWait { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override long GetTimestamp()
        {
            var runtime = Volatile.Read(ref _overloaded);
            return base.GetTimestamp() + (runtime is null ? 0 : 100 + 50 * Interlocked.Read(ref runtime.PublishedTicks));
        }

        public void Overload(MatchRuntime runtime)
        {
            Volatile.Write(ref _overloaded, runtime);
            _release.SetResult();
        }

        public Task WaitAsync(TimeSpan delay, CancellationToken ct)
        {
            if (FirstWait.TrySetResult())
            {
                return _release.Task.WaitAsync(ct);
            }
            // Other rooms use the normal tick interval on the same monotonic clock.
            Advance(delay);
            return Task.Delay(delay, ct);
        }
    }
}
