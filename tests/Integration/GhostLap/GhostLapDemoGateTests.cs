extern alias realtime;
using GhostLap.Contracts.Multiplayer;
using GhostLap.Multiplayer;
using GhostLap.Rules;
using GhostLap.Simulation;
using Integration.Tests.Support;
using Marrow.Infrastructure.Messaging;
using Marrow.Kernel.Security;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using realtime::GhostLapHost;
using Xunit;
using Xunit.Abstractions;

namespace Integration.Tests.GhostLap;

/// <summary>Skipped unless <c>GHOSTLAP_DEMO_GATE=1</c>: real wall-clock 2700-tick matches (about 90 s each) that Codex runs, not Kiro.</summary>
public sealed class DemoGateTheoryAttribute : TheoryAttribute
{
    public DemoGateTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("GHOSTLAP_DEMO_GATE") != "1")
        {
            Skip = "Set GHOSTLAP_DEMO_GATE=1 (see docs/architecture/11-ghostlap-backend-demo-readiness.md section 8).";
        }
    }
}

/// <summary>
/// Task 10.6, the backend demo-readiness gate, over the REAL Hosts.Realtime composition, real guest cookies issued by the real Hosts.Api, real
/// WebSocket SignalR clients and real PostgreSQL. It adds only what 10.0 to 10.5 did not already prove: rules identity <c>ghost-mp-v2</c> followed
/// through room admission, the receipt, snapshots, the durable result and the outbox, then through a rematch; the crash after a result commit
/// observed from a successor process; the production composition without test seams; and the real-time 2/3/4-player gate (Codex).
/// <para>
/// The quick tests replace ONLY the match clock (a logical clock whose waits advance it, so 2700 ticks take about a second) and nothing else:
/// admission, the receipt, the loop, snapshot fan-out, the finalizer and the hub are the production objects. Real-time play is the gated theory.
/// </para>
/// </summary>
[Collection(IdentityCollection.Name)]
[Trait("Category", "Integration")]
public sealed partial class GhostLapDemoGateTests(IdentityDatabase db, ITestOutputHelper output)
{
    private readonly GhostLapHubTests _kit = new(db);
    private static readonly Dictionary<string, string?> Fast = new() { ["GhostLap:Multiplayer:CountdownMilliseconds"] = "1000" };

    // ------------------------------------------------------------------ harness

    /// <summary>A match clock whose waits advance it. <see cref="Hold"/> parks the next wait (the loop is then at tick 0) until <see cref="Release"/>.</summary>
    private sealed class GateClock : TimeProvider
    {
        private long _milliseconds;
        private sealed record Pause(long At, TaskCompletionSource Release, TaskCompletionSource Parked);

        private Pause _pause = new(0, Completed(), new(TaskCreationOptions.RunContinuationsAsynchronously));
        public TaskCompletionSource Parked => Volatile.Read(ref _pause).Parked;

        private static TaskCompletionSource Completed()
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            tcs.SetResult();
            return tcs;
        }

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => Interlocked.Read(ref _milliseconds);

        public void Hold() => PauseAt(0);

        private Pause PauseAt(long timestamp)
        {
            var next = new Pause(timestamp, new(TaskCreationOptions.RunContinuationsAsynchronously),
                new(TaskCreationOptions.RunContinuationsAsynchronously));
            return Interlocked.Exchange(ref _pause, next);
        }

        public void Release() => Volatile.Read(ref _pause).Release.TrySetResult();

        public async Task AdvanceToAsync(long tick)
        {
            // This clock starts at zero for the first round. The scheduler rounds each 30 Hz due time up.
            var stopAt = (tick * TimestampFrequency + GameRulesV1.TickRate - 1) / GameRulesV1.TickRate;
            PauseAt(stopAt).Release.TrySetResult();
            await Parked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public async Task WaitAsync(TimeSpan delay, CancellationToken ct)
        {
            var gate = Volatile.Read(ref _pause);
            if (GetTimestamp() >= gate.At && !gate.Release.Task.IsCompleted)
            {
                gate.Parked.TrySetResult();
                await gate.Release.Task.WaitAsync(ct);
            }
            Interlocked.Add(ref _milliseconds, (long)Math.Ceiling(delay.TotalMilliseconds));
            await Task.Yield();
        }
    }

    private static Action<IServiceCollection> UseClock(GateClock clock, Func<IServiceProvider, MatchAuthorityStore>? store = null) => services =>
    {
        if (store is not null)
        {
            services.Replace(ServiceDescriptor.Singleton(store));
        }
        services.Replace(ServiceDescriptor.Singleton(sp => new MatchRegistry(
            sp.GetRequiredService<IOptions<MultiplayerOptions>>().Value, clock, sp.GetRequiredService<IRealtimeAuthority>(),
            sp.GetRequiredService<MatchAuthorityStore>(), clock.WaitAsync, sp.GetRequiredService<IMatchTransport>())));
    };

    private sealed class Harness : IAsyncDisposable
    {
        public required GhostLapHubTests.Stack Stack { get; init; }
        public bool OwnsStack { get; init; } = true;
        public List<GameClient> Players { get; } = [];
        public List<GameClient> Spectators { get; } = [];
        public string RoomId { get; set; } = "";
        public string RoomCode { get; set; } = "";
        public MatchRuntime? Runtime { get; set; }
        public MatchRegistry Registry => Stack.Hub.Services.GetRequiredService<MatchRegistry>();
        public IEnumerable<GameClient> All => Players.Concat(Spectators);
        public long ServerTick() => Runtime?.Engine.State.Tick ?? 0;

        public async ValueTask DisposeAsync()
        {
            foreach (var c in All)
            {
                c.Inputs.Writer.TryComplete();
                await c.DisposeAsync();
            }
            if (OwnsStack)
            {
                await Stack.DisposeAsync();
            }
        }
    }

    private async Task<Harness> OpenRoomAsync(GhostLapHubTests.Stack stack, int players, int spectators, bool ownsStack = true)
    {
        var h = new Harness { Stack = stack, OwnsStack = ownsStack };
        async Task<GameClient> Open()
        {
            var guest = await GhostLapHubTests.GuestAsync(stack);
            // The documented client contract: ping at least every 5 s (the server closes a silent connection after 15 s).
            var tab = await GhostLapHubTests.OpenAsync(stack, guest, configure: c => c.KeepAliveInterval = TimeSpan.FromSeconds(5));
            return new GameClient(tab, new NetworkConditions(), guest, h.ServerTick);
        }
        for (var i = 0; i < players; i++)
        {
            h.Players.Add(await Open());
        }
        var created = (await h.Players[0].Tab.CreateAsync()).Value!;
        h.Players[0].Joined = created;
        h.RoomId = created.View.RoomId;
        h.RoomCode = created.RoomCode;
        for (var i = 1; i < players; i++)
        {
            var joined = await h.Players[i].Tab.JoinAsync(created.RoomCode);
            Assert.True(joined.Ok, joined.Code);
            h.Players[i].Joined = joined.Value!;
        }
        for (var i = 0; i < spectators; i++)
        {
            var watcher = await Open();
            var joined = await watcher.Tab.JoinAsync(created.RoomCode, spectator: true);
            Assert.True(joined.Ok, joined.Code);
            watcher.Joined = joined.Value!;
            h.Spectators.Add(watcher);
        }
        return h;
    }

    /// <summary>Ready, start, and wait for the room to report a Running match other than <paramref name="previousMatchId"/>.</summary>
    private static async Task<SealedRoundView> StartRoundAsync(Harness h, string? previousMatchId)
    {
        foreach (var p in h.Players)
        {
            Assert.True((await p.Tab.ReadyAsync(h.RoomId)).Ok);
        }
        var started = await h.Players[0].Tab.StartAsync(h.RoomId);
        Assert.True(started.Ok, started.Code);
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until)
        {
            var reply = await h.Players[0].Tab.GetAsync(h.RoomId);
            if (reply is { Ok: true, Value: { Round: { } round, MatchStatus: MatchStatuses.Running } } && round.MatchId != previousMatchId)
            {
                h.Runtime = h.Registry.Find(Guid.Parse(round.MatchId))!;
                Assert.NotNull(h.Runtime);
                return round;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("no Running match");
    }

    /// <summary>
    /// One client object per match: a new filter, slot and input stream on the same connection. The rematch test fetches its new baseline via Resync.
    /// </summary>
    private async Task AttachAsync(Harness h, SealedRoundView round, bool rematch)
    {
        for (var i = 0; i < h.Players.Count; i++)
        {
            var old = h.Players[i];
            var client = old;
            if (rematch)
            {
                old.Inputs.Writer.TryComplete();
                await old.Stream!.WaitAsync(TimeSpan.FromSeconds(10));
                client = new GameClient(old.Tab, old.Net, old.Login, h.ServerTick) { Joined = old.Joined };
                h.Players[i] = client;
            }
            client.MatchId = round.MatchId;
            client.Slot = round.Slots.Single(s => s.MemberId == client.Joined.MemberId).Slot;
            client.OpenStream();
        }
        for (var i = 0; i < h.Spectators.Count; i++)
        {
            if (rematch)
            {
                var old = h.Spectators[i];
                h.Spectators[i] = new GameClient(old.Tab, old.Net, old.Login, h.ServerTick) { Joined = old.Joined };
            }
            h.Spectators[i].MatchId = round.MatchId;
        }
    }

    private static async Task<SnapshotFrame> ResyncAsync(GameClient c)
    {
        var reply = await c.Tab.Connection.InvokeAsync<GhostLapReply<SnapshotFrame>>(GhostLapWire.ResyncMethod);
        Assert.True(reply.Ok, reply.Code);
        Assert.True(c.OfferSnapshot(reply.Value!).Applied);
        return reply.Value!;
    }

    private static async Task Eventually(Func<bool> condition, string what, int timeoutMs = 15_000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException(what);
            }
            await Task.Delay(20);
        }
    }

    private static Task<GhostLapReply<MatchOutcomeView>> ReadResult(GameClient c, string matchId) =>
        c.Tab.Connection.InvokeAsync<GhostLapReply<MatchOutcomeView>>(GhostLapWire.GetMatchResultMethod, matchId);

    /// <summary>
    /// Everything that must be true of one finalized match, from the sealed round to the outbox: the rules identity on every frame and receipt,
    /// the durable result read back through the hub by every roster principal and equal to the last authoritative snapshot, one result and one
    /// MatchFinalizedV1 row, and no result visible to a spectator.
    /// </summary>
    private async Task<MatchOutcomeView> AssertFinalizedAsync(Harness h, SealedRoundView round, MatchRuntime runtime, int players)
    {
        await Eventually(() => runtime.Status == MatchStatuses.Finalized, "the result commits", 120_000);
        await Eventually(() => h.All.All(c => c.Last?.IsFinal == true), "every client receives the final snapshot", 30_000);
        var generation = h.Stack.Hub.Services.GetRequiredService<GhostLapRealtimeAuthority>().GenerationId.ToString("N");
        foreach (var c in h.All)
        {
            var frames = c.Frames.Where(f => f.Snapshot.MatchId == round.MatchId).ToArray();
            Assert.NotEmpty(frames);
            Assert.All(frames, f => Assert.Equal((MpRulesProfile.CurrentRulesId, GameRulesV1.LayoutVersion, generation),
                (f.Snapshot.RulesId, f.Snapshot.LayoutId, f.Snapshot.GenerationId)));
            Assert.Equal((2700L, true), (c.Last!.Tick, c.Last.IsFinal));
        }
        var spectator = h.Spectators.FirstOrDefault();
        if (spectator is not null)
        {
            var you = spectator.Frames.Last(f => f.Snapshot.MatchId == round.MatchId).You;
            Assert.Equal((RoomRoles.Spectator, false), (you.Role, you.CanControl));
            var watched = await ReadResult(spectator, round.MatchId); // a room member may read the durable outcome (read-only, no principal ids)
            Assert.True(watched.Ok, watched.Code);
            Assert.Equal(MatchOutcomeDecisions.Finalized, watched.Value!.Decision);
        }

        MatchOutcomeView? first = null;
        foreach (var c in h.Players)
        {
            var reply = await ReadResult(c, round.MatchId);
            Assert.True(reply.Ok, reply.Code);
            var outcome = reply.Value!;
            first ??= outcome;
            Assert.Equal(first.ResultDigest, outcome.ResultDigest);
            Assert.Equal((MatchOutcomeDecisions.Finalized, "elo-pair-v1", true), (outcome.Decision, outcome.RatingAlgorithmId, outcome.RatedEligible));
            Assert.Equal(players, outcome.Players.Count);
            var mine = outcome.Players.Single(p => p.MemberId == c.Joined.MemberId);
            Assert.Equal(c.Slot, mine.Slot);
            Assert.Equal(c.Last!.Players[c.Slot].TotalScore, mine.TotalScore); // the durable score is the last authoritative score
            Assert.Equal(Enumerable.Range(0, players).Order(), outcome.Players.Select(p => p.Slot).Order());
            Assert.Equal(1, outcome.Players.Min(p => p.CompetitivePlacement));
        }
        var matchId = Guid.Parse(round.MatchId);
        Assert.Equal(
            $"{MpRulesProfile.CurrentRulesId}|{GameRulesV1.LayoutVersion}|{MpRulesProfile.CurrentRulesId}+{GameRulesV1.LayoutVersion}|elo-pair-v1|ResultCommitted|{players}",
            await db.ScalarAsync<string>(
                "SELECT concat_ws('|', rules_id, layout_id, rules_hash, rating_algorithm_id, terminal_decision, cardinality(roster_subjects)) FROM ghostlap.match_session WHERE match_id = @m",
                ("m", matchId)));
        Assert.Equal(MpRulesProfile.CurrentRulesId, await db.ScalarAsync<string>(
            "SELECT result_document::jsonb ->> 'rulesId' FROM ghostlap.match_result WHERE match_id = @m", ("m", matchId)));
        Assert.Equal(MpRulesProfile.CurrentRulesId, await db.ScalarAsync<string>(
            "SELECT payload::jsonb -> 'result' ->> 'rulesId' FROM ghostlap.outbox WHERE aggregate_id = @a AND event_type = 'ghostlap.MatchFinalizedV1'",
            ("a", matchId.ToString("D"))));
        Assert.Equal((1L, 1L), (
            await db.ScalarAsync<long>("SELECT count(*) FROM ghostlap.match_result WHERE match_id = @m", ("m", matchId)),
            await db.ScalarAsync<long>("SELECT count(*) FROM ghostlap.outbox WHERE aggregate_id = @a", ("a", matchId.ToString("D")))));
        Assert.Equal(0L, await MatchFinalizationTests.GlobalInvariantViolationsAsync(db));
        return first!;
    }

    // ------------------------------------------------------------------ 1. production composition

    /// <summary>
    /// **Validates: Requirements 10.17, 10.20** The host as <c>Program.cs</c> composes it, with no probe policies: one MatchRegistry is admission,
    /// input sink, presence and resync; the real SignalR transport and cryptographic randomness are in place; the match registry carries no test
    /// hook; the Ghost Lap routes are exactly the hub; the hub refuses a connection without a guest session. The test project reaches the
    /// production assembly only through the same public composition (no test auth handler exists in <c>src/</c>, see the source scan below).
    /// </summary>
    [Fact]
    public async Task The_production_composition_has_one_owner_and_no_test_seam()
    {
        await using var host = new RealtimeHostFactory(db, IdentityApiFactory.NewMasterSecret(), registerDomainPolicies: false, registerProbes: false);
        var sp = host.Services;
        var registry = sp.GetRequiredService<MatchRegistry>();
        Assert.Same(registry, sp.GetRequiredService<IMatchStartAdmission>());
        Assert.Same(registry, sp.GetRequiredService<IGameplayInputSink>());
        Assert.Same(registry, sp.GetRequiredService<IMatchPresence>());
        Assert.Same(registry, sp.GetRequiredService<IMatchResync>());
        Assert.IsType<GhostLapSignalRTransport>(sp.GetRequiredService<IMatchTransport>());
        Assert.IsType<CryptoRoomRandom>(sp.GetRequiredService<IRoomRandom>());
        Assert.Same(TimeProvider.System, sp.GetRequiredService<TimeProvider>());
        Assert.Null(registry.RuntimeCreated);
        Assert.Same(sp.GetRequiredService<ActiveRoomOwner>(), sp.GetRequiredService<IActiveRoomOwner>());

        using (var scope = sp.CreateScope())
        {
            var resource = Assert.IsType<KindRoutingResourceAuthorizer>(scope.ServiceProvider.GetRequiredService<IResourceAuthorizer>());
            Assert.IsType<GhostLapAuthorizer>(resource.GhostLap);
            Assert.IsNotType<GhostLapAuthorizer>(resource.Fallback); // another domain's (or the identity deny-all) policy is still reachable
            Assert.IsType<KindRoutingMembershipAuthorizer>(scope.ServiceProvider.GetRequiredService<IMembershipAuthorizer>());
        }

        _ = host.Server;
        var routes = sp.GetServices<EndpointDataSource>().SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? "").ToArray();
        output.WriteLine("ROUTES " + string.Join(" ", routes.Order()));
        Assert.Contains(routes, r => r.StartsWith(GhostLapWire.HubPath, StringComparison.Ordinal));
        Assert.All(routes.Where(r => r.Contains("ghost", StringComparison.OrdinalIgnoreCase)),
            r => Assert.StartsWith(GhostLapWire.HubPath, r, StringComparison.Ordinal)); // no other gameplay route
        Assert.DoesNotContain(routes, r => r.Contains("test", StringComparison.OrdinalIgnoreCase) || r.Contains("debug", StringComparison.OrdinalIgnoreCase));

        // No guest session, no connection (the policy requires a guest principal).
        await using var anonymous = host.ConnectTo(GhostLapWire.HubPath, guestCookie: null, origin: "http://localhost");
        await Assert.ThrowsAnyAsync<Exception>(() => anonymous.StartAsync());
    }

    /// <summary>
    /// **Validates: Requirements 10.20** No test-only authentication bypass, anonymous gameplay route or test scheme exists in production source:
    /// the guest session scheme is the only way into the Ghost Lap hub, and nothing in <c>src/</c> installs a fake authentication handler.
    /// </summary>
    [Fact]
    public void Production_source_contains_no_test_authentication_or_anonymous_gameplay_route()
    {
        var root = RepositoryRoot();
        var offenders = new List<string>();
        foreach (var directory in new[] { "src/Hosts.Realtime", "src/Hosts.Api", "src/GhostLap", "src/GhostLap.Contracts" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, directory), "*.cs", SearchOption.AllDirectories)
                         .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
            {
                var text = File.ReadAllText(file);
                foreach (var forbidden in new[] { "AllowAnonymous", "AddScheme<", "TestAuthHandler", "X-Test-", "UseTestServer", "AuthenticationHandler<" })
                {
                    if (text.Contains(forbidden, StringComparison.Ordinal))
                    {
                        offenders.Add($"{Path.GetRelativePath(root, file)}: {forbidden}");
                    }
                }
            }
        }
        Assert.Empty(offenders);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TheSandsOfTime.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    // ------------------------------------------------------------------ 2. identity through a match and a rematch (quick)

    /// <summary>
    /// **Validates: Requirements 10.02, 10.04, 10.08, 10.13, 10.14, 10.16, 10.17** Four real sockets and a spectator. The sealed round, the
    /// receipt, every snapshot, the durable result and the outbox carry <c>ghost-mp-v2</c> on <c>arena-v1</c>; the durable scores equal the last
    /// authoritative snapshot. A real rematch over the same sockets issues a fresh match id, seed, receipt and initial state, is adopted by Resync,
    /// finalizes to its own result and outbox row, and leaves the first result untouched. (Match clock only is logical; see the class remarks.)
    /// </summary>
    [Fact]
    public async Task Rules_identity_ghost_mp_v2_flows_through_four_real_sockets_the_result_and_a_fresh_rematch()
    {
        var clock = new GateClock();
        clock.Hold();
        await using var stack = _kit.NewStack(Fast, UseClock(clock));
        await using var h = await OpenRoomAsync(stack, 4, 1);

        // Round 1, parked at tick 0.
        var r1 = await StartRoundAsync(h, null);
        await clock.Parked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var runtime1 = h.Runtime!;
        await AttachAsync(h, r1, rematch: false);
        Assert.Equal((MpRulesProfile.CurrentRulesId, GameRulesV1.LayoutVersion, $"{MpRulesProfile.CurrentRulesId}+{GameRulesV1.LayoutVersion}", 2700, "elo-pair-v1"),
            (r1.RulesId, r1.LayoutId, r1.RulesHash, r1.TargetTicks, r1.RatingAlgorithmId));
        Assert.Equal((r1.RulesId, r1.LayoutId, r1.Seed), (runtime1.Config.RulesId, runtime1.Config.LayoutId, runtime1.Config.Seed.ToString()));
        Assert.Equal("Started|ghost-mp-v2|arena-v1", await db.ScalarAsync<string>(
            "SELECT concat_ws('|', terminal_decision, rules_id, layout_id) FROM ghostlap.match_session WHERE match_id = @m", ("m", runtime1.Config.MatchId)));
        await Eventually(() => h.All.All(c => c.Last is not null), "tick-0 frames");
        Assert.All(h.All, c => Assert.Equal((0L, MpRulesProfile.CurrentRulesId, GameRulesV1.LayoutVersion), (c.Last!.Tick, c.Last.RulesId, c.Last.LayoutId)));
        foreach (var p in h.Players)
        {
            p.Send(1, 0, false, aim: 1);
        }
        await Eventually(() => runtime1.Engine.QueuedInputs == h.Players.Count, "all test inputs reach the mailbox at tick 0");
        clock.Release();
        var result1 = await AssertFinalizedAsync(h, r1, runtime1, 4);
        Assert.All(h.Players, p => Assert.Contains(p.Acks, a => a.LastAppliedSeq >= 1 && a.MatchId == r1.MatchId));
        var kinds = h.Players[0].AppliedEvents.Select(e => e.Kind).Distinct().Order().ToArray();
        Assert.Contains(MatchEventKinds.RoundFinished, kinds);
        output.WriteLine($"ROUND1 match={r1.MatchId} seed={r1.Seed} digest={result1.ResultDigest![..16]} eventKinds={string.Join(",", kinds)}");

        // Real rematch: back to the lobby, ready, start; parked at tick 0 of a fresh match.
        clock.Hold();
        var back = await h.Players[0].Tab.Connection.InvokeAsync<GhostLapReply<RoomCommandResult>>(GhostLapWire.RematchMethod, new RoomCommand("rematch-1", h.RoomId));
        Assert.True(back.Ok, back.Code);
        Assert.Equal((RoomPhases.Lobby, RoomNotices.RematchLobby), (back.Value!.View.Phase, back.Value.View.Notice));
        var r2 = await StartRoundAsync(h, r1.MatchId);
        await clock.Parked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var runtime2 = h.Runtime!;
        await AttachAsync(h, r2, rematch: true);
        Assert.NotEqual(r1.MatchId, r2.MatchId);
        Assert.NotEqual(r1.Seed, r2.Seed);
        Assert.NotEqual(r1.ConfigDigest, r2.ConfigDigest);
        Assert.NotSame(runtime1.Engine, runtime2.Engine);
        Assert.Equal((r1.RulesId, r1.LayoutId, r1.RatingAlgorithmId), (r2.RulesId, r2.LayoutId, r2.RatingAlgorithmId));
        Assert.Equal((0L, false), (runtime2.Engine.State.Tick, runtime2.Engine.State.IsFinal));
        Assert.All(runtime2.Engine.State.World.Players, p => Assert.Equal((0, 0), (p.TotalScore, p.Charge)));
        // Fresh state: every slot at its sealed spawn with nothing carried over (the tick-0 hash is not compared: it covers per-round slot identity).
        foreach (var slot in r2.Slots)
        {
            var p = runtime2.Engine.State.World.Players[slot.Slot];
            Assert.Equal((slot.SpawnXUnits * GameRulesV1.FixedOne, slot.SpawnYUnits * GameRulesV1.FixedOne, 0, 0, 0, 0),
                (p.XFixed, p.YFixed, p.TotalScore, p.Charge, p.Pickups, p.Bursts));
        }
        Assert.NotEqual(CandlePlan.Create(long.Parse(r1.Seed)).Candles.Select(c => (c.SiteIndex, c.ActivateTick)).ToArray(),
            CandlePlan.Create(long.Parse(r2.Seed)).Candles.Select(c => (c.SiteIndex, c.ActivateTick)).ToArray());
        Assert.Equal("1|2", await db.ScalarAsync<string>(
            "SELECT string_agg(round_ordinal::text, '|' ORDER BY round_ordinal) FROM ghostlap.match_session WHERE room_id = @r",
            ("r", Guid.ParseExact(h.RoomId, "N"))));
        Assert.Equal("ResultCommitted|Started", await db.ScalarAsync<string>(
            "SELECT string_agg(terminal_decision, '|' ORDER BY round_ordinal) FROM ghostlap.match_session WHERE room_id = @r", ("r", Guid.ParseExact(h.RoomId, "N"))));
        foreach (var c in h.All)
        {
            var frame = await ResyncAsync(c); // a client adopts the new match only through an explicit resync
            Assert.Equal((r2.MatchId, 0L, true, MpRulesProfile.CurrentRulesId), (frame.Snapshot.MatchId, frame.Snapshot.Tick, frame.IsResync, frame.Snapshot.RulesId));
        }
        foreach (var p in h.Players)
        {
            p.Send(0, 1, false, aim: 1);
        }
        await Eventually(() => runtime2.Engine.QueuedInputs == h.Players.Count, "all rematch inputs reach the mailbox at tick 0");
        clock.Release();
        var result2 = await AssertFinalizedAsync(h, r2, runtime2, 4);
        Assert.NotEqual(result1.ResultDigest, result2.ResultDigest);
        Assert.Equal(result1.ResultDigest, (await ReadResult(h.Players[0], r1.MatchId)).Value!.ResultDigest); // the first result is untouched
        Assert.Equal(2L, await db.ScalarAsync<long>(
            "SELECT count(DISTINCT event_id) FROM ghostlap.outbox WHERE aggregate_id = ANY(@ids)",
            ("ids", new[] { Guid.Parse(r1.MatchId).ToString("D"), Guid.Parse(r2.MatchId).ToString("D") })));
        output.WriteLine($"ROUND2 match={r2.MatchId} seed={r2.Seed} digest={result2.ResultDigest![..16]}");
    }

    // ------------------------------------------------------------------ 3. crash immediately after the result commit

    /// <summary>
    /// **Validates: Requirements 10.14, 10.15, 10.16** The process loses the commit acknowledgement and gives up (a crash immediately after the
    /// COMMIT, as the process sees it: one finalize attempt, the COMMIT reaches PostgreSQL, the reply is cut). The old process then stops and a
    /// successor Realtime host starts against the same database. The durable truth is ResultCommitted with one result and one outbox row, untouched by
    /// the successor's generation retirement; a roster principal reads the finalized result from the SUCCESSOR's hub; an outsider cannot; the lost
    /// room is a typed refusal and a fresh room works. The old process never reported an abort or a winner it did not have durable.
    /// </summary>
    [Fact]
    public async Task A_crash_after_the_result_commit_leaves_the_committed_result_readable_from_the_successor()
    {
        var secret = IdentityApiFactory.NewMasterSecret();
        await using var api = new IdentityApiFactory(db, secret);
        await using var proxy = new PgFaultProxy(db.RealtimeConnectionString);
        await using var proxied = NpgsqlDataSource.Create(proxy.ConnectionString(db.RealtimeConnectionString, "demo-proxied-pool"));
        var clock = new GateClock();
        clock.Hold();
        var settings = new Dictionary<string, string?>(Fast) { ["GhostLap:Multiplayer:FinalizeAttempts"] = "1", ["GhostLap:Multiplayer:GenerationCheckMilliseconds"] = "60000" };
        string matchId;
        string digestInDb;
        GhostLapHubTests.GuestLogin guestA;
        GhostLapHubTests.GuestLogin guestB;
        await using (var hostA = new RealtimeHostFactory(db, secret, settings,
                         customize: UseClock(clock, _ => MatchFinalizationTests.Store(proxied))))
        {
            var stackA = new GhostLapHubTests.Stack { Api = api, Hub = hostA };
            await using var h = await OpenRoomAsync(stackA, 2, 0, ownsStack: false);
            guestA = h.Players[0].Login;
            guestB = h.Players[1].Login;
            var round = await StartRoundAsync(h, null);
            await clock.Parked.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var runtime = h.Runtime!;
            matchId = round.MatchId;
            proxy.DownAfterDroppedCommit = true;
            proxy.Arm(PgFaultProxy.CommitFault.DropAfterCommit); // the next COMMIT is the finalizer's; the database path then vanishes
            clock.Release();
            await Eventually(() => runtime.Status is MatchStatuses.Unverified or MatchStatuses.Finalized or MatchStatuses.Aborted, "the finalizer gives up", 60_000);
            Assert.Equal(1, proxy.CommitsDroppedAfter);
            proxy.Down = false; // only so the assertions below can read through the old process's pool; the process had already given up
            Assert.Equal(MatchStatuses.Unverified, runtime.Status); // honest: the process does not know, and says so; it never says aborted
            Assert.Null(runtime.Result);
            Assert.Equal("ResultCommitted:1", await db.ScalarAsync<string>(
                "SELECT concat(terminal_decision, ':', terminal_version) FROM ghostlap.match_session WHERE match_id = @m", ("m", Guid.Parse(matchId))));
            digestInDb = (await db.ScalarAsync<string>("SELECT result_digest FROM ghostlap.match_result WHERE match_id = @m", ("m", Guid.Parse(matchId))))!;
        }

        // The successor process: new generation, retires everything unfinished, and must leave the committed receipt alone.
        await using var hostB = new RealtimeHostFactory(db, secret);
        var stackB = new GhostLapHubTests.Stack { Api = api, Hub = hostB };
        Assert.True(hostB.Services.GetRequiredService<GhostLapRealtimeAuthority>().IsValid);
        Assert.Equal("ResultCommitted:1:-", await db.ScalarAsync<string>(
            "SELECT concat(terminal_decision, ':', terminal_version, ':', coalesce(abort_reason, '-')) FROM ghostlap.match_session WHERE match_id = @m", ("m", Guid.Parse(matchId))));
        Assert.Equal((1L, 1L), (
            await db.ScalarAsync<long>("SELECT count(*) FROM ghostlap.match_result WHERE match_id = @m", ("m", Guid.Parse(matchId))),
            await db.ScalarAsync<long>("SELECT count(*) FROM ghostlap.outbox WHERE aggregate_id = @a AND event_type = 'ghostlap.MatchFinalizedV1'", ("a", Guid.Parse(matchId).ToString("D")))));

        await using var tabA = await GhostLapHubTests.OpenAsync(stackB, guestA);
        var outsider = await GhostLapHubTests.GuestAsync(stackB);
        await using var tabOutsider = await GhostLapHubTests.OpenAsync(stackB, outsider);
        var read = await tabA.Connection.InvokeAsync<GhostLapReply<MatchOutcomeView>>(GhostLapWire.GetMatchResultMethod, matchId);
        Assert.True(read.Ok, read.Code);
        Assert.Equal((MatchOutcomeDecisions.Finalized, digestInDb, "elo-pair-v1"), (read.Value!.Decision, read.Value.ResultDigest, read.Value.RatingAlgorithmId));
        Assert.Equal(2, read.Value.Players.Count);
        Assert.Equal(GhostLapFailure.ResultNotFound,
            (await tabOutsider.Connection.InvokeAsync<GhostLapReply<MatchOutcomeView>>(GhostLapWire.GetMatchResultMethod, matchId)).Code);
        // The room died with the process: typed refusal, and a fresh room is available (the rematch path after a crash is a new room).
        var lost = await tabA.Connection.InvokeAsync<GhostLapReply<RoomCommandResult>>(GhostLapWire.RematchMethod, new RoomCommand("rematch-lost", Guid.NewGuid().ToString("N")));
        Assert.Equal(GhostLapFailure.RoomUnavailable, lost.Code);
        Assert.True((await tabA.CreateAsync()).Ok);
        Assert.Equal(0L, await MatchFinalizationTests.GlobalInvariantViolationsAsync(db));
        _ = guestB;
    }

    [DemoGateTheory]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    public Task Real_time_matches_complete_over_real_sockets_with_results_and_rematch(int players, bool rematch) =>
        RunKestrelRoundsAsync(players, rematch);
}
