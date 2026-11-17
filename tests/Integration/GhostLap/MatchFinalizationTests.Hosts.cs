extern alias realtime;
using GhostLap.Contracts.Multiplayer;
using GhostLap.Multiplayer;
using Integration.Tests.Support;
using Marrow.Infrastructure.Realtime;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using realtime::GhostLapHost;
using Xunit;

namespace Integration.Tests.GhostLap;

/// <summary>
/// Task 10.4 with real Realtime hosts (the Marrow <see cref="RealtimeInstanceGuard"/> on its dedicated session, the durable generation service)
/// and real match loops. Each test runs its match through <see cref="MatchRegistry"/> on a fast logical clock (ticks are executed back to back;
/// tick count, not wall time, drives the game), so a full 2700-tick round takes about a second.
/// </summary>
public sealed partial class MatchFinalizationTests
{
    /// <summary>
    /// **Validates: Requirements 10.05, 10.14, 10.15** The guard-loss race. Only A's dedicated advisory session is killed; A's ordinary pool stays
    /// alive and A still believes it is the authority (probe 60 s, generation check 60 s). A finishes its match while successor B starts.
    /// <para>oldCommitsFirst: A's finalizer holds the receipt row when B's retirement arrives; B waits, A's result commits and survives B.</para>
    /// <para>successor first: B holds the receipt rows (retirement not yet committed) when A's finalizer arrives; A waits, then sees B's Aborted
    /// decision and writes nothing. In both orders there is exactly one durable decision and no result without its decision.</para>
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Guard_loss_race_with_the_old_pool_alive_has_one_truthful_decision_in_both_commit_orders(bool oldCommitsFirst)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = timeout.Token;
        var secret = IdentityApiFactory.NewMasterSecret();
        await using var hostA = new RealtimeHostFactory(db, secret, new()
        {
            ["Realtime:InstanceLockCheckSeconds"] = "60",
            ["GhostLap:Multiplayer:GenerationCheckMilliseconds"] = "60000",
        });
        var authorityA = hostA.Services.GetRequiredService<GhostLapRealtimeAuthority>();
        Assert.True(authorityA.IsValid);
        await using var oldPool = Source("old-pool");
        var storeA = Store(oldPool);
        var clock = new FastClock(hold: true);
        var registryA = new MatchRegistry(new MultiplayerOptions(), clock, authorityA, storeA, clock.WaitAsync);
        RealtimeHostFactory? hostB = null;
        try
        {
            var config = Seal(authorityA.GenerationId);
            Assert.Equal(StartAdmissionOutcome.Admitted, await registryA.AdmitAsync(config, ct));
            registryA.Activate(config.MatchId, [new SlotControl(0, 1, "c0"), new SlotControl(1, 1, "c1")]);
            var runtime = Assert.IsType<MatchRuntime>(registryA.Find(config.MatchId));
            await clock.FirstWait.Task.WaitAsync(ct);

            // Lose ONLY the old dedicated advisory session. The old process's ordinary pool is alive and A still thinks it is the authority.
            Assert.Equal(1L, await db.ScalarAsync<long>(
                "SELECT count(*) FROM (SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name = @n) t",
                ("n", RealtimeInstanceGuard.ApplicationName)));
            Assert.True(await db.ScalarAsync<long>("SELECT count(*) FROM pg_stat_activity WHERE application_name = 'old-pool'") >= 1);
            Assert.Equal("Active", await storeA.GenerationStatusAsync(authorityA.GenerationId, ct));
            Assert.True(authorityA.IsValid);

            Task<RealtimeHostFactory> startB() => Task.Run(() =>
            {
                var b = new RealtimeHostFactory(db, secret, new() { ["Realtime:InstanceLockAcquireSeconds"] = "20" });
                _ = b.Server;
                return b;
            });

            if (oldCommitsFirst)
            {
                await using var gate = await PauseAtAsync("old_finalize", "AFTER INSERT ON ghostlap.match_result", $"NEW.match_id = '{config.MatchId:D}'", 111, ct);
                clock.Release();
                var oldPid = await WaitBlockedAsync("old-pool", gate.BackendPid, ct);     // A holds the receipt row and its result row
                Assert.Equal(MatchStatuses.PendingFinalization, runtime.Status);
                var startingB = startB();
                await WaitBlockedAsync(GhostLapMatchDataSource.ApplicationName, oldPid, ct); // B's retirement waits for A's receipt row
                Assert.Equal("Started:0:-", await ReceiptAsync(config.MatchId));
                await gate.ReleaseAsync(ct);
                hostB = await startingB.WaitAsync(ct);
                await UntilAsync(() => runtime.Status != MatchStatuses.PendingFinalization, "A's finalizer settles", ct);
                Assert.Equal((MatchStatuses.Finalized, FinalizeOutcome.Committed), (runtime.Status, runtime.LastFinalizeOutcome));
                Assert.Equal("ResultCommitted:1:-", await ReceiptAsync(config.MatchId));
                Assert.Equal((1L, 1L), await CountsAsync(config.MatchId));
            }
            else
            {
                await using var gate = await PauseAtAsync("new_generation", "AFTER INSERT ON ghostlap.realtime_generation",
                    $"NEW.generation_id <> '{authorityA.GenerationId:D}'", 112, ct);
                var startingB = startB();
                var newPid = await WaitBlockedAsync(GhostLapMatchDataSource.ApplicationName, gate.BackendPid, ct); // B holds the receipt rows
                clock.Release();
                await WaitBlockedAsync("old-pool", newPid, ct);                                                    // A's finalizer waits for B
                Assert.Equal(MatchStatuses.PendingFinalization, runtime.Status);
                Assert.True(runtime.Engine.State.IsFinal);
                await gate.ReleaseAsync(ct);
                hostB = await startingB.WaitAsync(ct);
                await UntilAsync(() => runtime.Status != MatchStatuses.PendingFinalization, "A's finalizer settles", ct);
                Assert.Equal((MatchStatuses.Aborted, FinalizeOutcome.Aborted), (runtime.Status, runtime.LastFinalizeOutcome));
                Assert.Null(runtime.Result);
                Assert.Equal("Aborted:1:generation_retired", await ReceiptAsync(config.MatchId));
                Assert.Equal((0L, 0L), await CountsAsync(config.MatchId));
            }

            var authorityB = hostB.Services.GetRequiredService<GhostLapRealtimeAuthority>();
            Assert.True(authorityB.IsValid);
            Assert.Equal("Retired", await storeA.GenerationStatusAsync(authorityA.GenerationId, ct));
            // The stale process can learn the truth but cannot start or change anything any more.
            Assert.Equal(StartReceiptOutcome.GenerationRetired,
                (await storeA.CommitStartReceiptAsync(Seal(authorityA.GenerationId), authorityA.GenerationId, ct)).Outcome);
            var again = await storeA.FinalizeAsync(MatchResults.Build(config, runtime.Engine.State, runtime.Engine.Stats), authorityA.GenerationId, TimeProvider.System, ct);
            Assert.Equal(oldCommitsFirst ? FinalizeOutcome.AlreadyCommitted : FinalizeOutcome.Aborted, again.Outcome);
            // The successor's new epoch runs its own fresh match while the old receipt is decided.
            var storeB = hostB.Services.GetRequiredService<MatchAuthorityStore>();
            var fresh = Seal(authorityB.GenerationId);
            Assert.Equal(StartReceiptOutcome.Committed, (await storeB.CommitStartReceiptAsync(fresh, authorityB.GenerationId, ct)).Outcome);
            Assert.Equal(FinalizeOutcome.Committed, (await storeB.FinalizeAsync(Play(fresh), authorityB.GenerationId, TimeProvider.System, ct)).Outcome);
            Assert.Equal(0L, await GlobalInvariantViolationsAsync());
            output.WriteLine($"GUARD-LOSS-RACE oldFirst={oldCommitsFirst} A={runtime.Status}/{runtime.LastFinalizeOutcome} receipt={await ReceiptAsync(config.MatchId)}");
        }
        finally
        {
            clock.Release();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await registryA.StopAllAsync(stop.Token);
            if (hostB is not null)
            {
                await hostB.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// **Validates: Requirements 10.02, 10.15** Database unreachable (a TCP relay that refuses connections). At start: no receipt, so the room
    /// returns to its lobby (start_unavailable) and nothing runs. At finish: the round shows pending_finalization, then unverified after the bounded
    /// attempts; the receipt stays Started (never claimed aborted); the next generation aborts it unrated. If the database returns within the
    /// attempts, the result commits.
    /// </summary>
    [Fact]
    public async Task Database_down_at_start_starts_nothing_and_down_at_finish_is_pending_then_unverified_never_aborted()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = timeout.Token;
        var secret = IdentityApiFactory.NewMasterSecret();
        await using var proxy = new PgFaultProxy(db.RealtimeConnectionString);
        await using var proxied = NpgsqlDataSource.Create(proxy.ConnectionString(db.RealtimeConnectionString, "proxied-pool"));
        Guid unverifiedMatch;
        await using (var host = new RealtimeHostFactory(db, secret))
        {
            var authority = host.Services.GetRequiredService<GhostLapRealtimeAuthority>();
            var options = new MultiplayerOptions { FinalizeAttempts = 3, FinalizeInitialBackoffMilliseconds = 250, FinalizeMaxBackoffMilliseconds = 500 };
            var clock = new FastClock(hold: false);
            var roomTime = new FastClock(hold: false);
            var registry = new MatchRegistry(options, clock, authority, Store(proxied), clock.WaitAsync);
            var owner = new ActiveRoomOwner(options, roomTime, registry, registry, new CryptoRoomRandom(), authority.GenerationId, registry);
            try
            {
                // Start while the database is unreachable: no receipt, back to the lobby, nothing runs.
                var (caller, joined, round) = BeginCountdown(owner);
                proxy.Down = true;
                roomTime.Advance(TimeSpan.FromMilliseconds(options.CountdownMilliseconds + 1));
                await owner.SweepAsync(ct);
                var lobby = Value(owner.GetRoom(caller, joined.View.RoomId));
                Assert.Equal((RoomPhases.Lobby, RoomNotices.StartUnavailable, (string?)null), (lobby.Phase, lobby.Notice, lobby.MatchStatus));
                Assert.Null(registry.Find(Guid.Parse(round.MatchId)));
                Assert.Null(await ReceiptAsync(Guid.Parse(round.MatchId)));

                // Database back: the identical sealed round starts (same match id); then it goes away again before the end of the round.
                proxy.Down = false;
                Value(owner.StartCountdown(caller, new("start-2", joined.View.RoomId)));
                Assert.Equal(round.MatchId, Value(owner.GetRoom(caller, joined.View.RoomId)).Round!.MatchId);
                var runtime = await AdmitAndRunAsync(owner, registry, roomTime, round, ct, beforeActivation: () => proxy.Down = true);
                await UntilAsync(() => runtime.Status != MatchStatuses.PendingFinalization && runtime.Status != MatchStatuses.Running, "finalization settles", ct);
                Assert.Equal((MatchStatuses.Unverified, FinalizeOutcome.Unavailable, 3), (runtime.Status, runtime.LastFinalizeOutcome, runtime.FinalizeAttemptsMade));
                Assert.Null(runtime.Result);
                unverifiedMatch = runtime.Config.MatchId;
                Assert.Equal("Started:0:-", await ReceiptAsync(unverifiedMatch)); // provisional: not a result, and not an abort either
                await owner.SweepAsync(ct);
                Assert.Equal(MatchStatuses.Unverified, Value(owner.GetRoom(caller, joined.View.RoomId)).MatchStatus);

                // A second match: the database comes back after the first failed attempt, so the retry commits.
                proxy.Down = false;
                Value(owner.Rematch(caller, new("rematch", joined.View.RoomId)));
                var second = await ReadyAndStartAsync(owner, caller, joined, ct);
                var retried = await AdmitAndRunAsync(owner, registry, roomTime, second, ct, beforeActivation: () => proxy.Down = true);
                await UntilAsync(() => retried.LastFinalizeOutcome == FinalizeOutcome.Unavailable, "first attempt fails", ct);
                proxy.Down = false;
                await UntilAsync(() => retried.Status == MatchStatuses.Finalized, "retry commits", ct);
                Assert.True(retried.FinalizeAttemptsMade >= 2);
                Assert.Equal(FinalizeOutcome.Committed, retried.LastFinalizeOutcome);
                Assert.Equal((1L, 1L), await CountsAsync(retried.Config.MatchId));
            }
            finally
            {
                proxy.Down = false;
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await registry.StopAllAsync(stop.Token);
            }
        }

        // No authorized finalizer remains for the unverified match: the next generation's retirement is its durable decision (unrated, no result).
        await using var successor = new RealtimeHostFactory(db, secret);
        Assert.True(successor.Services.GetRequiredService<GhostLapRealtimeAuthority>().IsValid);
        Assert.Equal("Aborted:1:generation_retired", await ReceiptAsync(unverifiedMatch));
        Assert.Equal((0L, 0L), await CountsAsync(unverifiedMatch));
        Assert.Equal(0L, await GlobalInvariantViolationsAsync());
    }

    /// <summary>
    /// **Validates: Requirements 10.14, 10.15** Commit acknowledgement faults on the real wire. (a) The COMMIT reaches PostgreSQL and commits, but
    /// its reply is cut: the finalizer sees a failure, retries, finds the committed identical result (AlreadyCommitted) and reports finalized; the
    /// committed winner survives a successor. (b) The connection dies before the COMMIT is delivered: nothing commits, the retry commits once.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Lost_commit_acknowledgement_and_lost_commit_are_both_resolved_by_the_retry(bool lostAfterCommit)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = timeout.Token;
        var secret = IdentityApiFactory.NewMasterSecret();
        await using var proxy = new PgFaultProxy(db.RealtimeConnectionString);
        await using var proxied = NpgsqlDataSource.Create(proxy.ConnectionString(db.RealtimeConnectionString, "proxied-pool"));
        MatchResultRecord committed;
        await using (var host = new RealtimeHostFactory(db, secret))
        {
            var authority = host.Services.GetRequiredService<GhostLapRealtimeAuthority>();
            var options = new MultiplayerOptions { FinalizeAttempts = 4, FinalizeInitialBackoffMilliseconds = 50 };
            var clock = new FastClock(hold: false);
            var registry = new MatchRegistry(options, clock, authority, Store(proxied), clock.WaitAsync);
            try
            {
                var config = Seal(authority.GenerationId, 3);
                Assert.Equal(StartAdmissionOutcome.Admitted, await registry.AdmitAsync(config, ct));
                proxy.Arm(lostAfterCommit ? PgFaultProxy.CommitFault.DropAfterCommit : PgFaultProxy.CommitFault.DropBeforeCommit);
                registry.Activate(config.MatchId, [new SlotControl(0, 1, "c0"), new SlotControl(1, 1, "c1"), new SlotControl(2, 1, null)]);
                var runtime = Assert.IsType<MatchRuntime>(registry.Find(config.MatchId));
                await UntilAsync(() => runtime.Status == MatchStatuses.Finalized, "finalized after the retry", ct);
                Assert.Equal(2, runtime.FinalizeAttemptsMade);
                Assert.Equal(lostAfterCommit ? FinalizeOutcome.AlreadyCommitted : FinalizeOutcome.Committed, runtime.LastFinalizeOutcome);
                Assert.Equal((lostAfterCommit ? 1 : 0, lostAfterCommit ? 0 : 1), (proxy.CommitsDroppedAfter, proxy.CommitsDroppedBefore));
                Assert.Equal("ResultCommitted:1:-", await ReceiptAsync(config.MatchId));
                Assert.Equal((1L, 1L), await CountsAsync(config.MatchId));
                committed = runtime.Result!;
                Assert.Equal(committed.Digest, await db.ScalarAsync<string>(
                    "SELECT result_digest FROM ghostlap.match_result WHERE match_id = @m", ("m", config.MatchId)));
                Assert.Equal("t|eligible|{1,1,3}|{f,f,t}", await ResultShapeAsync(config.MatchId));
            }
            finally
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await registry.StopAllAsync(stop.Token);
            }
        }
        await using var successor = new RealtimeHostFactory(db, secret);
        Assert.True(successor.Services.GetRequiredService<GhostLapRealtimeAuthority>().IsValid);
        Assert.Equal("ResultCommitted:1:-", await ReceiptAsync(committed.MatchId)); // the committed winner survives the crash/replacement
        Assert.Equal((1L, 1L), await CountsAsync(committed.MatchId));
    }

    /// <summary>
    /// **Validates: Requirements 10.13, 10.16** Delayed finalization keeps the start-pinned rating algorithm (the registry's own default differs);
    /// a rematch is refused while the result is pending, then returns the room to its lobby once; the next round goes through ready/start and gets a
    /// fresh match id, seed, state and receipt, and its own independent result and event.
    /// </summary>
    [Fact]
    public async Task Rematch_after_a_finalized_match_starts_a_fresh_match_with_its_own_result_and_the_sealed_rating_algorithm()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = timeout.Token;
        await using var host = new RealtimeHostFactory(db, IdentityApiFactory.NewMasterSecret());
        var authority = host.Services.GetRequiredService<GhostLapRealtimeAuthority>();
        await using var pool = Source("rematch-pool");
        var roomOptions = new MultiplayerOptions { RatingAlgorithmId = "elo-pair-v1" };
        var deploymentDefault = new MultiplayerOptions { RatingAlgorithmId = "elo-pair-v9" }; // what a later deployment would seal
        var clock = new FastClock(hold: false);
        var roomTime = new FastClock(hold: false);
        var registry = new MatchRegistry(deploymentDefault, clock, authority, Store(pool), clock.WaitAsync);
        var owner = new ActiveRoomOwner(roomOptions, roomTime, registry, registry, new CryptoRoomRandom(), authority.GenerationId, registry);
        try
        {
            var (caller, joined, first) = BeginCountdown(owner);
            MatchRuntime firstRuntime;
            await using (var gate = await PauseAtAsync("delayed_finalize", "AFTER INSERT ON ghostlap.match_result", $"NEW.match_id = '{Guid.Parse(first.MatchId):D}'", 113, ct))
            {
                firstRuntime = await AdmitAndRunAsync(owner, registry, roomTime, first, ct);
                await WaitBlockedAsync("rematch-pool", gate.BackendPid, ct);
                await owner.SweepAsync(ct);
                Assert.Equal(MatchStatuses.PendingFinalization, Value(owner.GetRoom(caller, joined.View.RoomId)).MatchStatus);
                Assert.Equal(GhostLapFailure.FinalizationPending, owner.Rematch(caller, new("too-early", joined.View.RoomId)).Code);
                await gate.ReleaseAsync(ct);
                await UntilAsync(() => firstRuntime.Status == MatchStatuses.Finalized, "first match finalized", ct);
            }
            await owner.SweepAsync(ct);
            Assert.Equal(MatchStatuses.Finalized, Value(owner.GetRoom(caller, joined.View.RoomId)).MatchStatus);
            Assert.Equal("elo-pair-v1", firstRuntime.Result!.Document.RatingAlgorithmId);

            var back = Value(owner.Rematch(caller, new("rematch", joined.View.RoomId)));
            Assert.Equal((RoomPhases.Lobby, RoomNotices.RematchLobby, (string?)null), (back.View.Phase, back.View.Notice, back.View.MatchStatus));
            Assert.All(back.View.Members, m => Assert.False(m.Ready));
            var duplicate = Value(owner.Rematch(caller, new("rematch-again", joined.View.RoomId)));
            Assert.Equal("already_lobby", duplicate.Detail);
            Assert.Equal(GhostLapFailure.NotReady, owner.StartCountdown(caller, new("start-unready", joined.View.RoomId)).Code);

            var second = await ReadyAndStartAsync(owner, caller, joined, ct);
            Assert.NotEqual(first.MatchId, second.MatchId);
            Assert.NotEqual(first.Seed, second.Seed);
            Assert.Equal("elo-pair-v1", second.RatingAlgorithmId);
            var secondRuntime = await AdmitAndRunAsync(owner, registry, roomTime, second, ct);
            Assert.NotSame(firstRuntime.Engine, secondRuntime.Engine);
            await UntilAsync(() => secondRuntime.Status == MatchStatuses.Finalized, "second match finalized", ct);

            Assert.Equal("1|elo-pair-v1|2|elo-pair-v1", await db.ScalarAsync<string>(
                """
                SELECT string_agg(concat_ws('|', s.round_ordinal, r.rating_algorithm_id), '|' ORDER BY s.round_ordinal)
                  FROM ghostlap.match_session s JOIN ghostlap.match_result r USING (match_id)
                 WHERE s.room_id = @room
                """, ("room", Guid.ParseExact(joined.View.RoomId, "N"))));
            Assert.Equal(2L, await db.ScalarAsync<long>(
                "SELECT count(DISTINCT event_id) FROM ghostlap.outbox WHERE aggregate_id = ANY(@ids)",
                ("ids", new[] { Guid.Parse(first.MatchId).ToString("D"), Guid.Parse(second.MatchId).ToString("D") })));
            Assert.NotEqual(firstRuntime.Result!.Digest, secondRuntime.Result!.Digest);
            Assert.Equal(0L, await GlobalInvariantViolationsAsync());
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await registry.StopAllAsync(stop.Token);
        }
    }

    /// <summary>
    /// **Validates: Requirements 10.14, 10.15, 10.16, 10.20** Over the real hub with real guest sessions: a roster principal reads pending, then the
    /// finalized result (no guest principal ids in the reply), and an aborted outcome with its reason; an outsider and an unknown match get
    /// result_not_found; Rematch is typed (lobby: already_lobby; no room: room_unavailable).
    /// </summary>
    [Fact]
    public async Task The_hub_serves_durable_outcomes_to_roster_principals_only_and_rematch_is_typed()
    {
        var kit = new GhostLapHubTests(db);
        await using var stack = kit.NewStack();
        var a = await GhostLapHubTests.GuestAsync(stack);
        var b = await GhostLapHubTests.GuestAsync(stack);
        var outsider = await GhostLapHubTests.GuestAsync(stack);
        await using var tabA = await GhostLapHubTests.OpenAsync(stack, a);
        await using var tabOutsider = await GhostLapHubTests.OpenAsync(stack, outsider);
        var authority = stack.Hub.Services.GetRequiredService<GhostLapRealtimeAuthority>();
        var store = stack.Hub.Services.GetRequiredService<MatchAuthorityStore>();
        SealedRoundConfig Roster() => RoundSealing.Seal(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), Guid.CreateVersion7(), 1,
            [new RosterEntry("m0", a.Subject), new RosterEntry("m1", b.Subject)], new MultiplayerOptions(), authority.GenerationId);
        Task<GhostLapReply<MatchOutcomeView>> Read(GhostLapHubTests.Tab tab, string id) =>
            tab.Connection.InvokeAsync<GhostLapReply<MatchOutcomeView>>(GhostLapWire.GetMatchResultMethod, id);

        var config = Roster();
        await StartAsync(store, config);
        var pending = await Read(tabA, config.MatchId.ToString("N"));
        Assert.True(pending.Ok, pending.Code);
        Assert.Equal((MatchOutcomeDecisions.Pending, 0), (pending.Value!.Decision, pending.Value.Players.Count));

        var record = Play(config);
        Assert.Equal(FinalizeOutcome.Committed, (await store.FinalizeAsync(record, authority.GenerationId, TimeProvider.System, default)).Outcome);
        var finalized = await Read(tabA, config.MatchId.ToString("D"));
        Assert.True(finalized.Ok, finalized.Code);
        Assert.Equal((MatchOutcomeDecisions.Finalized, record.Digest, true, "elo-pair-v1"),
            (finalized.Value!.Decision, finalized.Value.ResultDigest, finalized.Value.RatedEligible, finalized.Value.RatingAlgorithmId));
        Assert.Equal([1, 1], finalized.Value.Players.Select(p => p.CompetitivePlacement));
        var wire = System.Text.Json.JsonSerializer.Serialize(finalized.Value);
        Assert.DoesNotContain(a.Subject, wire, StringComparison.Ordinal);
        Assert.DoesNotContain(b.Subject, wire, StringComparison.Ordinal);

        Assert.Equal(GhostLapFailure.ResultNotFound, (await Read(tabOutsider, config.MatchId.ToString("N"))).Code);
        Assert.Equal(GhostLapFailure.ResultNotFound, (await Read(tabA, Guid.NewGuid().ToString("N"))).Code);
        Assert.Equal(GhostLapFailure.InvalidRequest, (await Read(tabA, "not-a-match")).Code);

        var aborted = Roster();
        await StartAsync(store, aborted);
        Assert.Equal(AbortOutcome.Aborted, (await store.AbortAsync(aborted.MatchId, authority.GenerationId, AbortReasons.Overload, default)).Outcome);
        var abortedView = await Read(tabA, aborted.MatchId.ToString("N"));
        Assert.Equal((MatchOutcomeDecisions.Aborted, AbortReasons.Overload, false, 0),
            (abortedView.Value!.Decision, abortedView.Value.AbortReason, abortedView.Value.RatedEligible, abortedView.Value.Players.Count));

        var noRoom = await tabA.Connection.InvokeAsync<GhostLapReply<RoomCommandResult>>(
            GhostLapWire.RematchMethod, new RoomCommand("rematch-0", Guid.NewGuid().ToString("N")));
        Assert.Equal(GhostLapFailure.RoomUnavailable, noRoom.Code);
        var created = (await tabA.CreateAsync()).Value!;
        var lobby = await tabA.Connection.InvokeAsync<GhostLapReply<RoomCommandResult>>(
            GhostLapWire.RematchMethod, new RoomCommand("rematch-1", created.View.RoomId));
        Assert.True(lobby.Ok, lobby.Code);
        Assert.Equal("already_lobby", lobby.Value!.Detail);
    }

    // ---------------------------------------------------------------- host helpers

    private (RoomCaller Caller, JoinedRoom Joined, SealedRoundView Round) BeginCountdown(ActiveRoomOwner owner)
    {
        var host = new RoomCaller("guest:" + Guid.NewGuid(), Guid.NewGuid().ToString("N"));
        var peer = new RoomCaller("guest:" + Guid.NewGuid(), Guid.NewGuid().ToString("N"));
        var joined = Value(owner.CreateRoom(host, new("create")));
        Value(owner.JoinRoom(peer, new("join", joined.RoomCode, false)));
        Peers[joined.View.RoomId] = peer;
        Value(owner.SetReady(host, new("ready", joined.View.RoomId, true)));
        Value(owner.SetReady(peer, new("ready", joined.View.RoomId, true)));
        var countdown = Value(owner.StartCountdown(host, new("start", joined.View.RoomId))).View;
        Assert.Equal(RoomPhases.Countdown, countdown.Phase);
        return (host, joined, Assert.IsType<SealedRoundView>(countdown.Round));
    }

    private readonly Dictionary<string, RoomCaller> Peers = [];

    private Task<SealedRoundView> ReadyAndStartAsync(ActiveRoomOwner owner, RoomCaller caller, JoinedRoom joined, CancellationToken ct)
    {
        var room = joined.View.RoomId;
        Value(owner.SetReady(caller, new("ready-" + Guid.NewGuid().ToString("N"), room, true)));
        Value(owner.SetReady(Peers[room], new("ready-" + Guid.NewGuid().ToString("N"), room, true)));
        var view = Value(owner.StartCountdown(caller, new("start-" + Guid.NewGuid().ToString("N"), room))).View;
        Assert.Equal(RoomPhases.Countdown, view.Phase);
        return Task.FromResult(Assert.IsType<SealedRoundView>(view.Round));
    }

    /// <summary>Elapses the countdown, lets the sweep commit the receipt and activate the match, and returns the running match.</summary>
    private static async Task<MatchRuntime> AdmitAndRunAsync(
        ActiveRoomOwner owner, MatchRegistry registry, FastClock roomTime, SealedRoundView round, CancellationToken ct, Action? beforeActivation = null)
    {
        var matchId = Guid.Parse(round.MatchId);
        if (beforeActivation is not null)
        {
            registry.RuntimeCreated = _ => beforeActivation(); // after the receipt committed, before the loop can run
        }
        roomTime.Advance(TimeSpan.FromMilliseconds(3001));
        await owner.SweepAsync(ct);
        registry.RuntimeCreated = null;
        var runtime = Assert.IsType<MatchRuntime>(registry.Find(matchId));
        Assert.True(runtime.Activated);
        return runtime;
    }

    private static T Value<T>(OwnerResult<T> result) where T : class
    {
        Assert.True(result.Ok, result.Code);
        return Assert.IsType<T>(result.Value);
    }

    private async Task<int> WaitBlockedAsync(string app, int blockerPid, CancellationToken ct)
    {
        var blockedPid = 0;
        await UntilAsync(async () =>
            (blockedPid = await db.ScalarAsync<int>(
                "SELECT pid FROM pg_stat_activity WHERE application_name = @app AND @pid = ANY(pg_blocking_pids(pid)) LIMIT 1",
                ("app", app), ("pid", blockerPid))) != 0, $"{app} never waited on PostgreSQL backend {blockerPid}", ct);
        return blockedPid;
    }

    private static Task UntilAsync(Func<bool> condition, string message, CancellationToken ct) =>
        UntilAsync(() => Task.FromResult(condition()), message, ct);

    private static async Task UntilAsync(Func<Task<bool>> condition, string message, CancellationToken ct)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            while (!await condition())
            {
                await Task.Delay(10, wait.Token);
            }
        }
        catch (OperationCanceledException) when (wait.IsCancellationRequested)
        {
            throw new TimeoutException(message);
        }
    }

    /// <summary>A test-only trigger that parks the matching transaction on an advisory lock the test holds; production SQL does every write.</summary>
    private async Task<DatabaseGate> PauseAtAsync(string name, string triggerEvent, string condition, int key, CancellationToken ct)
    {
        var trigger = $"verify_{name}";
        var table = triggerEvent.Split(" ON ")[1];
        await ExecuteAsync($"""
            CREATE FUNCTION ghostlap.{trigger}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                PERFORM pg_advisory_xact_lock(642219, {key});
                RETURN NEW;
            END $$;
            CREATE TRIGGER {trigger} {triggerEvent} FOR EACH ROW WHEN ({condition})
                EXECUTE FUNCTION ghostlap.{trigger}();
            """, ct);
        var connection = await db.Owner.OpenConnectionAsync(ct);
        var tx = await connection.BeginTransactionAsync(ct);
        await using (var hold = new NpgsqlCommand("SELECT pg_advisory_xact_lock(642219, @key)", connection, tx))
        {
            hold.Parameters.AddWithValue("key", key);
            await hold.ExecuteNonQueryAsync(ct);
        }
        return new DatabaseGate(connection, tx, () => ExecuteAsync($"DROP TRIGGER {trigger} ON {table}; DROP FUNCTION ghostlap.{trigger}()", CancellationToken.None));
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        await using var command = db.Owner.CreateCommand(sql);
        command.CommandTimeout = 12;
        await command.ExecuteNonQueryAsync(ct);
    }

    private sealed class DatabaseGate(NpgsqlConnection connection, NpgsqlTransaction tx, Func<Task> cleanup) : IAsyncDisposable
    {
        public int BackendPid => connection.ProcessID;

        public Task ReleaseAsync(CancellationToken ct) => tx.RollbackAsync(ct);

        public async ValueTask DisposeAsync()
        {
            await using (connection)
            {
                await tx.DisposeAsync();
            }
            await cleanup();
        }
    }

    /// <summary>
    /// A monotonic logical clock for tests. <see cref="WaitAsync"/> (the match loop's wait) advances it by the requested delay, so due ticks run back to
    /// back; with <c>hold</c> the first wait parks until <see cref="Release"/>. Timers (finalizer backoff) are real.
    /// </summary>
    internal sealed class FastClock : TimeProvider
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _milliseconds;

        public FastClock(bool hold)
        {
            if (!hold)
            {
                _release.SetResult();
            }
        }

        public TaskCompletionSource FirstWait { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => Interlocked.Read(ref _milliseconds);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _milliseconds, (long)Math.Ceiling(by.TotalMilliseconds));

        public void Release() => _release.TrySetResult();

        public async Task WaitAsync(TimeSpan delay, CancellationToken ct)
        {
            FirstWait.TrySetResult();
            await _release.Task.WaitAsync(ct);
            Advance(delay);
            await Task.Yield();
        }
    }
}
