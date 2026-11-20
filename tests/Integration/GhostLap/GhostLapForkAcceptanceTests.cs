extern alias realtime;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using GhostLap.Contracts.Multiplayer;
using GhostLap.Contracts.Solo;
using GhostLap.Rules;
using GhostLap.Simulation;
using GhostLap.Solo;
using GhostLap.Timeline;
using Integration.Tests.Support;
using Marrow.Infrastructure.Identity.Policy;
using Marrow.Timeline;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using realtime::GhostLapHost;

namespace Integration.Tests.GhostLap;

public sealed partial class GhostLapSoloCheckpointTests
{
    private static readonly JsonSerializerOptions AcceptanceJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Fork_final_acceptance_real_Kestrel_overlap_cold_owner_journey()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = timeout.Token;
        var secret = IdentityApiFactory.NewMasterSecret();
        await using var api = new IdentityApiFactory(db, secret);
        var owner = api.NewBrowser();
        var subject = (await owner.PostAsync("/auth/guest/start")).Str("subject");
        var cookie = owner.Cookies[Flows.GuestCookie];
        var foreign = api.NewBrowser();
        await foreign.PostAsync("/auth/guest/start");
        var otherCookie = foreign.Cookies[Flows.GuestCookie];
        await db.ScalarAsync<object>("CREATE EXTENSION IF NOT EXISTS pg_stat_statements");
        await using var provider = ChallengeServices();
        var engine = provider.GetRequiredService<ITimelineEngine<GameState, GameInput, GameContext>>();
        var store = new SoloOriginalStore(provider.GetRequiredService<NpgsqlDataSource>());
        var authorizer = new OwnerResourceAuthorizer(new SoloOwnershipLookup(store));
        var originals = new SoloOriginals(engine, store, authorizer);
        var branches = new SoloBranches(engine, store, authorizer);
        var actor = GuestActor(subject);
        var source = await ChallengeSourceAsync(originals, actor, 42, ct);
        var sourceId = source.Original.Timeline.Value.ToString("D");
        var fingerprint = await ChallengeFingerprintAsync(source.Original.Timeline.Value);
        var key = "acceptance-" + Guid.NewGuid().ToString("N");
        var received = new List<(ChallengeFrameView Frame, double Ms)>();
        var rejected = new ConcurrentQueue<SoloInputRejected>();
        ChallengeResultView result;
        SoloRunDiagnostics diagnostics;
        int oldPid;
        await using (var host = await Kestrel.StartAsync(db, secret, db.RealtimeSoloConnectionString, ct))
        {
            oldPid = host.Pid;
            await using var hub = await host.ConnectAsync(cookie, ct);
            await using var other = await host.ConnectAsync(otherCookie, ct);
            var listed = await Read<SoloOriginalView[]>(host, cookie, GhostLapSoloWire.OriginalsRoute, ct);
            Assert.Contains(listed, x => x.Id == sourceId);
            var inspected = await Read<SoloInspectView>(host, cookie, $"{GhostLapSoloWire.OriginalsRoute}/{sourceId}/inspect/1140", ct);
            AssertGhost(inspected.Frame, source.States[1140]);
            var denied = await other.InvokeAsync<GhostLapReply<ChallengePrepared>>("PrepareFork", new PrepareForkRequest(sourceId, 1140), ct);
            Assert.Equal(SoloFailure.NotFound, denied.Code);
            Assert.Equal(denied, await other.InvokeAsync<GhostLapReply<ChallengePrepared>>("PrepareFork", new PrepareForkRequest(Guid.NewGuid().ToString("D"), 1140), ct));
            var prep = Stopwatch.StartNew();
            var ready = Value(await hub.InvokeAsync<GhostLapReply<ChallengePrepared>>("PrepareFork", new PrepareForkRequest(sourceId, 1140), ct));
            output.WriteLine($"PREP {prep.Elapsed.TotalMilliseconds:F2} ms {JsonSerializer.Serialize(ready, AcceptanceJson)}");
            Assert.Equal((sourceId, "Fork", 1140L, 1141L, source.Original.FinalStateHash, "ghost-v1", "arena-v1"),
                (ready.OriginalId, ready.Kind, ready.ForkTick, ready.FirstInputTick, ready.FinalStateHash, ready.RulesVersion, ready.LayoutVersion));
            var frames = Channel.CreateUnbounded<(ChallengeFrameView, double)>();
            var ended = new TaskCompletionSource<SoloRunEnded>(TaskCreationOptions.RunContinuationsAsynchronously);
            var watch = Stopwatch.StartNew();
            hub.On<ChallengeFrameView>("ChallengeFrame", f => frames.Writer.TryWrite((f, watch.Elapsed.TotalMilliseconds)));
            hub.On<SoloRunEnded>("ChallengeEnded", e => ended.TrySetResult(e));
            hub.On<SoloInputRejected>("InputRejected", e => rejected.Enqueue(e));
            var started = Value(await hub.InvokeAsync<GhostLapReply<ChallengeStarted>>("StartChallenge", new StartChallengeRequest(key), ct));
            Assert.Equal(("Fork", 1140L, 1141L, 42L, source.Original.FinalStateHash),
                (started.Kind, started.ForkTick, started.FirstInputTick, started.Seed, started.SourceFinalStateHash));
            AssertGhost(started.Ghost, source.States[1140]);
            var plan = CandlePlan.Create(source.Original.Seed);
            Assert.Equal(JsonSerializer.Serialize(ChallengeMapping.ToWire(WorldFrame.Of(source.States[1140]), false, plan)), JsonSerializer.Serialize(started.Living));
            // Full canonical bytes (including fields not exported to the renderer) at the actual persisted child fork point.
            var restored = (await engine.RestoreAsync(new TimelineId(Guid.Parse(started.ChallengeId)), 1140, ct)).Value;
            Assert.Equal(GameStateCodec.Instance.EncodeCanonical(source.States[1140]), GameStateCodec.Instance.EncodeCanonical(restored.State));
            Assert.Equal(inspected.Frame.StateHash, restored.StateHash);
            Assert.False(Value(await other.InvokeAsync<GhostLapReply<bool>>("CancelChallenge", ct)));
            Assert.Equal(ChallengeFailure.NotReady, (await other.InvokeAsync<GhostLapReply<ChallengeStarted>>("StartChallenge", new StartChallengeRequest(key), ct)).Code);
            var inputs = Channel.CreateUnbounded<SoloInputItem>();
            var stream = hub.InvokeAsync<GhostLapReply<InputStreamSummary>>("StreamChallengeInputs", inputs.Reader, ct);
            var seq = started.Living.AckedInputSeq;
            var sent = 0;
            Task? overlap = null;
            var previous = started.Living;
            var detour = 0;
            long firstRead = 0, firstReadTick = 0, lastRead = 0, lastReadTick = 0;
            while (true)
            {
                var (frame, at) = await frames.Reader.ReadAsync(ct);
                Assert.InRange(frame.Tick, 1141, 2700);
                Assert.True(received.Count == 0 || frame.Tick > received[^1].Frame.Tick);
                received.Add((frame, at));
                AssertGhost(frame.Ghost, source.States[(int)frame.Tick]);
                Assert.Equal(frame.Tick, frame.Living.Tick);
                Assert.Equal(frame.Living.TotalScore - frame.Ghost.TotalScore, frame.Delta);
                if (frame.Tick >= 1250 && firstReadTick == 0) { firstRead = await ChallengeReadStatsAsync(); firstReadTick = frame.Tick; }
                if (frame.Tick >= 1450 && overlap is null) overlap = MultiplayerOverlapAsync(host, api, ct);
                if (frame.Tick >= 2500 && lastReadTick == 0) { lastRead = await ChallengeReadStatsAsync(); lastReadTick = frame.Tick; }
                if (frame.Final) break;
                var live = frame.Living;
                var active = source.Context.Plan.ActiveAt(live.Tick + 1).Where(c => live.ActiveCandleIds.Contains(c.Id));
                var target = active.OrderBy(c => Math.Abs((long)c.Site.X-live.XFixed)+Math.Abs((long)c.Site.Y-live.YFixed)).FirstOrDefault();
                if (live.XFixed == previous.XFixed && live.YFixed == previous.YFixed && detour == 0) detour = 20;
                var dx = target is null ? 0 : target.Site.X-live.XFixed;
                var dy = target is null ? 0 : target.Site.Y-live.YFixed;
                if (detour > 0) { detour--; (dx, dy) = live.Tick/50%2 == 0 ? (0, 1) : (1, -1); }
                var press = live.Charge == 4 && !live.BurstLatch;
                await inputs.Writer.WriteAsync(new SoloInputItem(++seq, live.Tick+1, (sbyte)Math.Sign(dx), (sbyte)Math.Sign(dy), press), ct);
                sent++;
                previous = live;
            }
            inputs.Writer.Complete();
            var ingress = Value(await stream);
            var ending = await ended.Task.WaitAsync(ct);
            Assert.Equal("final", ending.Reason);
            diagnostics = ending.Diagnostics!;
            Assert.Equal(1560, diagnostics.Ticks);
            Assert.Equal(1141, received[0].Frame.Tick);
            Assert.Equal(2700, received[^1].Frame.Tick);
            Assert.Equal(sent, ingress.Received);
            Assert.NotNull(overlap);
            // Only normal living Append reconstructs its last checkpoint once per transition; no ghost load is mixed in.
            Assert.InRange(lastRead-firstRead-(lastReadTick-firstReadTick), -3L, 3L);
            result = (await WaitForkFinishAsync(hub, key, ct)).Result!;
            Assert.Equal(("Fork", 1140L, sourceId, received[^1].Frame.Living.TotalScore), (result.Kind, result.ForkTick, result.OriginalId, result.LivingScore));
            Assert.NotEqual(result.ChildFinalStateHash, result.SourceFinalStateHash);
            Assert.Equal(result, (await WaitForkFinishAsync(hub, key, ct)).Result);
            Assert.Equal(SoloFailure.NotFound, Value(await hub.InvokeAsync<GhostLapReply<ChallengeFinishView>>("FinishChallenge", key, ct)).Code);
            Assert.Equal(SoloFailure.NotFound, Value(await other.InvokeAsync<GhostLapReply<ChallengeFinishView>>("FinishFork", key, ct)).Code);
            output.WriteLine($"LIVE pid {oldPid} client start-to-final {received[^1].Ms:F3}ms first-to-final {received[^1].Ms-received[0].Ms:F3}ms frames {received.Count}; ingress {JsonSerializer.Serialize(ingress)}; diagnostics {JsonSerializer.Serialize(diagnostics)}; result {JsonSerializer.Serialize(result)}");
            output.WriteLine($"HISTORY quiet checkpoint reads {lastRead-firstRead}, living transitions {lastReadTick-firstReadTick}; refusals {JsonSerializer.Serialize(rejected.ToArray())}");
            Metrics("SERVER commit gaps", received.Select(x => x.Frame.Pacing!.CommitElapsedMs).ToArray(), true);
            Metrics("SERVER send gaps", received.Select(x => x.Frame.Pacing!.SendElapsedMs).ToArray(), true);
            Metrics("CLIENT arrival gaps", received.Select(x => x.Ms).ToArray(), true);
            Metrics("SERVER schedule lag", received.Select(x => x.Frame.Pacing!.ScheduleLagMs).ToArray(), false);
            Assert.InRange(received[^1].Frame.Pacing!.CommitElapsedMs, 51900, 65000);
            await overlap;
            host.AssertLogHygiene(db.RuntimePasswords.Append(secret).Append(cookie).Append(otherCookie));
        }
        // Small realistic three-child catalog, with two cheap one-transition canonical fixtures.
        foreach (var suffix in new[] { "catalog-a", "catalog-b" })
        {
            var extra = (await branches.ForkAsync(actor, source.Original.Timeline, 2699, SoloBranchKinds.ForkKey(key+suffix), ct)).Value;
            Assert.True((await extra.CommitTickAsync(ct)).IsSuccess);
            Assert.True((await extra.CompleteAsync(ct)).IsSuccess);
        }
        Assert.Equal(fingerprint, await ChallengeFingerprintAsync(source.Original.Timeline.Value));
        Assert.Equal(subject, (await owner.PostAsync("/auth/guest/start", csrfCookie: Flows.GuestCsrf)).Str("subject"));
        await using var cold = await Kestrel.StartAsync(db, secret, db.RealtimeSoloConnectionString, ct);
        Assert.NotEqual(oldPid, cold.Pid);
        await using var recovered = await cold.ConnectAsync(cookie, ct);
        Assert.Equal(result, (await WaitForkFinishAsync(recovered, key, ct)).Result);
        Assert.Contains((await Read<SoloOriginalView[]>(cold, cookie, GhostLapSoloWire.OriginalsRoute, ct)), x => x.Id == sourceId);
        var catWatch = Stopwatch.StartNew(); var queries = await ChallengeSelectStatsAsync();
        var page = await Read<SoloChildPageView>(cold, cookie, GhostLapSoloWire.ChildrenRoute+"?limit=3", ct);
        output.WriteLine($"CATALOG {page.Items.Length} children {catWatch.Elapsed.TotalMilliseconds:F2}ms SELECTs {await ChallengeSelectStatsAsync()-queries}");
        Assert.Contains(page.Items, x => x.Id == result.ChallengeId && x.Kind == "Fork" && x.Verified);
        Assert.Equal(result, await Read<ChallengeResultView>(cold, cookie, GhostLapSoloWire.ChildrenRoute+"/"+result.ChallengeId, ct));
        var replay = (await engine.ReplayAsync(new TimelineId(Guid.Parse(result.ChallengeId)), ct)).Value;
        Assert.Equal((HistoryStatus.Verified, 2700L, result.ChildFinalStateHash, result.ChildLastRecordHash), (replay.Status, replay.FinalPosition, replay.FinalStateHash, replay.LastRecordHash));
        Assert.Equal(1560L, await db.ScalarAsync<long>("SELECT count(*) FROM marrow_timeline.steps WHERE timeline_id=@id", ("id", Guid.Parse(result.ChallengeId))));
        Assert.Equal("1141|2700", await db.ScalarAsync<string>("SELECT min(position)::text || '|' || max(position)::text FROM marrow_timeline.steps WHERE timeline_id=@id", ("id", Guid.Parse(result.ChallengeId))));
        var all = new List<SoloGhostFrame>(); var traceWatch = Stopwatch.StartNew();
        SoloSourceIdentity? identity = null;
        for (long tick = 0; tick <= 2700; tick += 300)
        {
            var chunk = await Read<SoloTraceView>(cold, cookie, $"{GhostLapSoloWire.OriginalsRoute}/{sourceId}/trace?from={tick}&count=300", ct);
            identity ??= chunk.Source; Assert.Equal(identity, chunk.Source);
            Assert.Equal(tick == 2700 ? null : tick+300, chunk.NextTick);
            foreach (var frame in chunk.Frames) { Assert.Equal(all.Count, frame.Tick); AssertGhost(frame, source.States[(int)frame.Tick]); all.Add(frame); }
        }
        Assert.Equal(2701, all.Count);
        output.WriteLine($"TRACE all {all.Count} exact frames {traceWatch.Elapsed.TotalMilliseconds:F2}ms identity {JsonSerializer.Serialize(identity)}");
        var repeat = await Read<SoloTraceView>(cold, cookie, $"{GhostLapSoloWire.OriginalsRoute}/{sourceId}/trace?from=1100&count=100", ct);
        Assert.Equal(JsonSerializer.Serialize(all.Skip(1100).Take(100)), JsonSerializer.Serialize(repeat.Frames));
        Assert.Equal(JsonSerializer.Serialize(all[1140]), JsonSerializer.Serialize((await Read<SoloInspectView>(cold, cookie, $"{GhostLapSoloWire.OriginalsRoute}/{sourceId}/inspect/1140", ct)).Frame));
        var childTraceWatch = Stopwatch.StartNew();
        var childFrames = new List<SoloGhostFrame>();
        for (long tick = 0; tick <= 2700; tick += 300)
        {
            var chunk = await Read<SoloChildTraceView>(cold, cookie, $"{GhostLapSoloWire.ChildrenRoute}/{result.ChallengeId}/trace?from={tick}&count=300", ct);
            Assert.Equal((result.ChallengeId, sourceId, "Fork", 1140L, result.SourceFinalStateHash, result.ChildFinalStateHash),
                (chunk.ChildId, chunk.SourceId, chunk.Kind, chunk.ForkTick, chunk.SourceFinalStateHash, chunk.ChildFinalStateHash));
            Assert.Equal(identity!.Versions, chunk.Versions);
            Assert.Equal(tick == 2700 ? null : tick+300, chunk.NextTick);
            foreach (var frame in chunk.Frames) { Assert.Equal(childFrames.Count, frame.Tick); childFrames.Add(frame); }
        }
        Assert.Equal(2701, childFrames.Count);
        Assert.Equal(result.ChildFinalStateHash, childFrames[^1].StateHash);
        Assert.Equal(JsonSerializer.Serialize(all[1140]), JsonSerializer.Serialize(childFrames[1140]));
        output.WriteLine($"CHILD TRACE 2701 bounded replay frames {childTraceWatch.Elapsed.TotalMilliseconds:F2}ms");
        var compareWatch = Stopwatch.StartNew();
        foreach (var tick in new[] { 0, 1140, 1141, 1920, 2699, 2700 })
        {
            var pointWatch = Stopwatch.StartNew();
            var comparison = await Read<SoloComparisonView>(cold, cookie, $"{GhostLapSoloWire.ChildrenRoute}/{result.ChallengeId}/comparison?from={tick}&count=1", ct);
            var point = Assert.Single(comparison.Points);
            var childState = (await engine.RestoreAsync(new TimelineId(Guid.Parse(result.ChallengeId)), tick, ct)).Value;
            Assert.Equal(source.States[tick].Players[0].TotalScore, point.SourceScore);
            Assert.Equal(childState.State.Players[0].TotalScore, point.ChildScore);
            Assert.Equal(point.ChildScore-point.SourceScore, point.Delta);
            Assert.Equal(childState.StateHash, point.ChildStateHash);
            AssertGhost(childFrames[tick], childState.State);
            if (tick <= 1140) Assert.Equal(point.SourceStateHash, point.ChildStateHash);
            output.WriteLine($"COMPARE {pointWatch.Elapsed.TotalMilliseconds:F2}ms {JsonSerializer.Serialize(point)}");
        }
        output.WriteLine($"COMPARE six including independent restores {compareWatch.Elapsed.TotalMilliseconds:F2}ms");
        compareWatch.Restart(); queries = await ChallengeSelectStatsAsync();
        var curve = await Read<SoloComparisonView>(cold, cookie, $"{GhostLapSoloWire.ChildrenRoute}/{result.ChallengeId}/comparison?from=0&count=271&stride=10", ct);
        Assert.Equal(271, curve.Points.Length); Assert.Null(curve.NextTick);
        Assert.Equal(result.ChildFinalStateHash, curve.Points[^1].ChildStateHash);
        output.WriteLine($"CURVE 271 exact score samples {compareWatch.Elapsed.TotalMilliseconds:F2}ms SELECTs {await ChallengeSelectStatsAsync()-queries}");
        Assert.Equal(fingerprint, await ChallengeFingerprintAsync(source.Original.Timeline.Value));
        output.WriteLine($"COLD pid {cold.Pid}; canonical replay verified; 1560 child steps 1141..2700; source fingerprint unchanged {fingerprint}");
    }

    [Fact]
    public async Task Fork_security_real_Kestrel_expiry_integrity_roles_and_solo_disabled()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = timeout.Token;
        var secret = IdentityApiFactory.NewMasterSecret();
        await using var api = new IdentityApiFactory(db, secret);
        var owner = api.NewBrowser(); var subject = (await owner.PostAsync("/auth/guest/start")).Str("subject");
        var foreign = api.NewBrowser(); await foreign.PostAsync("/auth/guest/start");
        var cookie = owner.Cookies[Flows.GuestCookie]; var otherCookie = foreign.Cookies[Flows.GuestCookie];
        await using var provider = ChallengeServices();
        var engine = provider.GetRequiredService<ITimelineEngine<GameState, GameInput, GameContext>>();
        var dataSource = provider.GetRequiredService<NpgsqlDataSource>();
        await using (var role = dataSource.CreateCommand("SELECT current_user || '|' || pg_has_role(current_user,'ghostlap_runtime','member')::text || '|' || pg_has_role(current_user,'marrow_timeline_runtime','member')::text || '|' || (SELECT rolsuper FROM pg_roles WHERE rolname=current_user)::text"))
            Assert.Equal("sands_realtime_solo|true|true|false", (string)(await role.ExecuteScalarAsync(ct))!);
        var store = new SoloOriginalStore(dataSource); var auth = new OwnerResourceAuthorizer(new SoloOwnershipLookup(store));
        var originals = new SoloOriginals(engine, store, auth); var branches = new SoloBranches(engine, store, auth);
        var source = await ChallengeSourceAsync(originals, GuestActor(subject), 42, ct);
        var fingerprint = await ChallengeFingerprintAsync(source.Original.Timeline.Value);
        var key = "security-"+Guid.NewGuid().ToString("N");
        var child = (await branches.ForkAsync(GuestActor(subject), source.Original.Timeline, 2699, SoloBranchKinds.ForkKey(key), ct)).Value;
        Assert.True((await child.CommitTickAsync(ct)).IsSuccess); var completed = (await child.CompleteAsync(ct)).Value;
        var extra = (await branches.ForkAsync(GuestActor(subject), source.Original.Timeline, 2699, SoloBranchKinds.ForkKey(key+"extra"), ct)).Value;
        Assert.True((await extra.CommitTickAsync(ct)).IsSuccess); Assert.True((await extra.CompleteAsync(ct)).IsSuccess);
        await using (var host = await Kestrel.StartAsync(db, secret, db.RealtimeSoloConnectionString, ct))
        {
            var expired = api.NewBrowser(); await expired.PostAsync("/auth/guest/start");
            var expiredCookie = expired.Cookies[Flows.GuestCookie];
            await db.ScalarAsync<object>("UPDATE marrow_identity.guest_sessions SET expires_at=now()-interval '1 second' WHERE id=@id", ("id", Guid.Parse(expiredCookie.Split('.')[0])));
            foreach (var path in new[] { $"{GhostLapSoloWire.OriginalsRoute}/{source.Original.Timeline.Value:D}/inspect/1140", $"{GhostLapSoloWire.OriginalsRoute}/{source.Original.Timeline.Value:D}/trace", GhostLapSoloWire.ChildrenRoute,
                $"{GhostLapSoloWire.ChildrenRoute}/{completed.Timeline.Value:D}", $"{GhostLapSoloWire.ChildrenRoute}/{completed.Timeline.Value:D}/comparison", $"{GhostLapSoloWire.ChildrenRoute}/{completed.Timeline.Value:D}/trace" })
            { using var denied = await host.GetAsync(path, expiredCookie, ct); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode); }
            await Assert.ThrowsAnyAsync<Exception>(async () => { await using var denied = await host.ConnectAsync(expiredCookie, ct); });
            await using var hub = await host.ConnectAsync(cookie, ct);
            await AcceptanceSecurityAsync(host, hub, owner, cookie, otherCookie, source.Original.Timeline.Value.ToString("D"), completed.Timeline.Value.ToString("D"), key, ct);
            Assert.Equal(fingerprint, await ChallengeFingerprintAsync(source.Original.Timeline.Value));
            output.WriteLine("EXPIRED cookie refused on all history routes and real WebSocket; actual solo login verified non-superuser with intended memberships; accepted source unchanged.");
        }
        await using var disabled = await Kestrel.StartAsync(db, secret, null, ct);
        using var missing = await disabled.GetAsync(GhostLapSoloWire.ChildrenRoute, otherCookie, ct); Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        await using var multiplayer = await disabled.ConnectAsync(otherCookie, ct, GhostLapWire.HubPath);
        Assert.True((await multiplayer.InvokeAsync<GhostLapReply<JoinedRoom>>(GhostLapWire.CreateRoomMethod, new CreateRoomRequest(Guid.NewGuid().ToString("N")), ct)).Ok);
        output.WriteLine("SOLO DISABLED: separate Production Kestrel exposes no solo routes; authenticated multiplayer creates a room.");
    }

    private static async Task<T> Read<T>(Kestrel host, string cookie, string path, CancellationToken ct)
    {
        using var response = await host.GetAsync(path, cookie, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(AcceptanceJson, ct))!;
    }

    private static async Task<ChallengeFinishView> WaitForkFinishAsync(HubConnection hub, string key, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = Value(await hub.InvokeAsync<GhostLapReply<ChallengeFinishView>>("FinishFork", key, ct));
            if (result.Status == "Completed") return result;
            Assert.True(attempt < 60, JsonSerializer.Serialize(result));
            await Task.Delay(250, ct);
        }
    }

    private void Metrics(string name, double[] values, bool gaps)
    {
        var samples = gaps ? values.Zip(values.Skip(1), (a,b) => b-a).ToArray() : values;
        output.WriteLine($"{name} ms p50/p95/p99/max {Stat.P(samples,.5):F3}/{Stat.P(samples,.95):F3}/{Stat.P(samples,.99):F3}/{samples.Max():F3}");
    }

    private async Task MultiplayerOverlapAsync(Kestrel host, IdentityApiFactory api, CancellationToken ct)
    {
        var players = new List<(HubConnection Hub, JoinedRoom Joined, Channel<GameplayInput> Inputs, Task<GhostLapReply<InputStreamSummary>> Stream, ConcurrentQueue<(SnapshotFrame Frame, double Ms)> Frames)>();
        var watch = Stopwatch.StartNew();
        string roomId = "", code = "";
        try
        {
            for (var i=0; i<4; i++)
            {
                var guest = api.NewBrowser(); await guest.PostAsync("/auth/guest/start");
                var hub = await host.ConnectAsync(guest.Cookies[Flows.GuestCookie], ct, GhostLapWire.HubPath);
                var frames = new ConcurrentQueue<(SnapshotFrame, double)>();
                hub.On<SnapshotFrame>(GhostLapWire.SnapshotClientMethod, f => frames.Enqueue((f, watch.Elapsed.TotalMilliseconds)));
                var joined = i == 0
                    ? Value(await hub.InvokeAsync<GhostLapReply<JoinedRoom>>(GhostLapWire.CreateRoomMethod, new CreateRoomRequest(Guid.NewGuid().ToString("N")), ct))
                    : Value(await hub.InvokeAsync<GhostLapReply<JoinedRoom>>(GhostLapWire.JoinRoomMethod, new JoinRoomRequest(Guid.NewGuid().ToString("N"), code, false), ct));
                roomId = joined.View.RoomId; code = joined.RoomCode;
                var inputs = Channel.CreateUnbounded<GameplayInput>();
                var stream = hub.InvokeAsync<GhostLapReply<InputStreamSummary>>(GhostLapWire.StreamInputsMethod, inputs.Reader, ct);
                players.Add((hub, joined, inputs, stream, frames));

            }
            foreach (var player in players)
                Value(await player.Hub.InvokeAsync<GhostLapReply<RoomCommandResult>>(GhostLapWire.SetReadyMethod, new SetReadyRequest(Guid.NewGuid().ToString("N"), roomId, true), ct));
            var start = Value(await players[0].Hub.InvokeAsync<GhostLapReply<RoomCommandResult>>(GhostLapWire.StartCountdownMethod, new RoomCommand(Guid.NewGuid().ToString("N"), roomId), ct));
            var round = start.View.Round!;
            Assert.Equal("ghost-mp-v2", round.RulesId);
            var begin = watch.Elapsed.TotalMilliseconds;
            var seq = new int[4];
            while (watch.Elapsed.TotalMilliseconds-begin < 23000)
            {
                for (var i=0; i<players.Count; i++)
                {
                    var p = players[i]; var last = p.Frames.LastOrDefault().Frame;
                    if (last is null) continue;
                    p.Inputs.Writer.TryWrite(new GameplayInput(round.MatchId, p.Joined.ControllerEpoch, last.Snapshot.Tick+1, ++seq[i], (sbyte)(seq[i]/30%2==0?1:-1), (sbyte)(i%2), false));
                }
                await Task.Delay(33, ct);
            }
            for (var i=0; i<players.Count; i++)
            {
                var p = players[i]; p.Inputs.Writer.Complete(); var ingress = Value(await p.Stream);
                var frames = p.Frames.ToArray();
                Assert.True(frames[^1].Frame.Snapshot.Tick >= 500);
                Assert.True(ingress.Received > 400 && ingress.Rejected < ingress.Received/2);
                Assert.Equal(round.MatchId, frames[^1].Frame.Snapshot.MatchId);
                Metrics($"MP player {i} delivery gaps", frames.Select(f => f.Ms).ToArray(), true);
                output.WriteLine($"MP player {i} ticks {frames[0].Frame.Snapshot.Tick}..{frames[^1].Frame.Snapshot.Tick}, frames {frames.Length}, ingress {JsonSerializer.Serialize(ingress)}");
            }
            output.WriteLine($"MP four-player overlap {watch.Elapsed.TotalSeconds:F3}s; profile {round.RulesId}; room {roomId}; isolated match {round.MatchId}");
        }
        finally
        {
            foreach (var p in players)
            {
                await p.Hub.InvokeAsync<GhostLapReply<RoomCommandResult>>(GhostLapWire.LeaveRoomMethod, new RoomCommand(Guid.NewGuid().ToString("N"), roomId), ct);
                await p.Hub.DisposeAsync();
            }
        }
    }

    private async Task AcceptanceSecurityAsync(Kestrel host, HubConnection ownerHub, TestBrowser owner, string cookie, string otherCookie, string source, string child, string key, CancellationToken ct)
    {
        var routes = new[] {
            $"{GhostLapSoloWire.OriginalsRoute}/{source}/inspect/1140", $"{GhostLapSoloWire.OriginalsRoute}/{source}/trace?count=1",
            $"{GhostLapSoloWire.ChildrenRoute}/{child}", $"{GhostLapSoloWire.ChildrenRoute}/{child}/comparison?count=1", $"{GhostLapSoloWire.ChildrenRoute}/{child}/trace?count=1" };
        foreach (var path in routes)
        {
            using var foreign = await host.GetAsync(path, otherCookie, ct);
            using var unknown = await host.GetAsync(path.Replace(source, Guid.NewGuid().ToString("D")).Replace(child, Guid.NewGuid().ToString("D")), otherCookie, ct);
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode); Assert.Equal(foreign.StatusCode, unknown.StatusCode);
            Assert.Equal(await foreign.Content.ReadAsStringAsync(ct), await unknown.Content.ReadAsStringAsync(ct));
            foreach (var bad in new string?[] { null, "invalid", cookie[..^8]+"AAAAAAAA" })
            {
                using var denied = await host.GetAsync(path, bad, ct); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            }
        }
        Assert.Empty((await Read<SoloChildPageView>(host, otherCookie, GhostLapSoloWire.ChildrenRoute, ct)).Items);
        // Isolated fixture mutation only: trigger disabled briefly, restored before source immutability check.
        // Use separate unfinished source for unsupported and corrupted metadata tests, never mutate accepted source.
        await using var provider = ChallengeServices();
        var engine = provider.GetRequiredService<ITimelineEngine<GameState, GameInput, GameContext>>();
        var store = new SoloOriginalStore(provider.GetRequiredService<NpgsqlDataSource>());
        var originals = new SoloOriginals(engine, store, new OwnerResourceAuthorizer(new SoloOwnershipLookup(store)));
        var subject = (await db.ScalarAsync<string>("SELECT owner_subject_id FROM ghostlap.solo_originals WHERE timeline_id=@id", ("id", Guid.Parse(source))))!;
        var unfinished = (await originals.StartAsync(GuestActor(subject), 42, 2, "bad-source-"+Guid.NewGuid().ToString("N"), ct)).Value;
        var unfinishedId = unfinished.Timeline.Value.ToString("D");
        Assert.Equal("not_complete", (await ownerHub.InvokeAsync<GhostLapReply<ChallengePrepared>>("PrepareFork", new PrepareForkRequest(unfinishedId, 1140), ct)).Code);
        using (var unreadable = await host.GetAsync($"{GhostLapSoloWire.OriginalsRoute}/{unfinishedId}/trace?count=1", cookie, ct)) Assert.Equal(HttpStatusCode.NotFound, unreadable.StatusCode);
        var corruptFixture = await ChallengeSourceAsync(originals, GuestActor(subject), 43, ct);
        var corruptId = corruptFixture.Original.Timeline.Value;
        // Corrupt only a separate completed canonical fixture; the accepted source is never modified.
        await db.ScalarAsync<object>("ALTER TABLE ghostlap.solo_originals DISABLE TRIGGER USER");
        try
        {
            await db.ScalarAsync<object>("UPDATE ghostlap.solo_originals SET final_state_hash=repeat('a',64) WHERE timeline_id=@id", ("id", corruptId));
            var corrupt = await ownerHub.InvokeAsync<GhostLapReply<ChallengePrepared>>("PrepareFork", new PrepareForkRequest(corruptId.ToString("D"), 1140), ct);
            Assert.False(corrupt.Ok); Assert.Contains(corrupt.Code, new[] { "verification_failed", "corrupt_history" });
            await db.ScalarAsync<object>("UPDATE ghostlap.solo_originals SET rules_version='unsupported' WHERE timeline_id=@id", ("id", corruptId));
            Assert.Equal("version_unsupported", (await ownerHub.InvokeAsync<GhostLapReply<ChallengePrepared>>("PrepareFork", new PrepareForkRequest(corruptId.ToString("D"), 1140), ct)).Code);
            using var incompatible = await host.GetAsync($"{GhostLapSoloWire.OriginalsRoute}/{corruptId:D}/trace?count=1", cookie, ct);
            Assert.Equal(HttpStatusCode.Conflict, incompatible.StatusCode);
        }
        finally { await db.ScalarAsync<object>("ALTER TABLE ghostlap.solo_originals ENABLE TRIGGER USER"); }
        var incompatibleChild = await db.ScalarAsync<Guid>("SELECT timeline_id FROM ghostlap.solo_branches WHERE source_timeline_id=@source AND timeline_id<>@child AND status='Complete' LIMIT 1", ("source", Guid.Parse(source)), ("child", Guid.Parse(child)));
        await db.ScalarAsync<object>("ALTER TABLE ghostlap.solo_branches DISABLE TRIGGER USER");
        try
        {
            await db.ScalarAsync<object>("UPDATE ghostlap.solo_branches SET rules_version='unsupported' WHERE timeline_id=@id", ("id", incompatibleChild));
            using var badComparison = await host.GetAsync($"{GhostLapSoloWire.ChildrenRoute}/{incompatibleChild:D}/comparison?count=1", cookie, ct);
            Assert.Equal(HttpStatusCode.Conflict, badComparison.StatusCode);
            Assert.Contains("version_unsupported", await badComparison.Content.ReadAsStringAsync(ct));
        }
        finally
        {
            await db.ScalarAsync<object>("UPDATE ghostlap.solo_branches SET rules_version='ghost-v1' WHERE timeline_id=@id", ("id", incompatibleChild));
            await db.ScalarAsync<object>("ALTER TABLE ghostlap.solo_branches ENABLE TRIGGER USER");
        }
        var revoked = new List<HubConnection>();
        try
        {
            for (var i=0; i<2; i++) revoked.Add(await host.ConnectAsync(cookie, ct));
            Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync("/auth/guest/signout", csrfCookie: Flows.GuestCsrf)).Status);
            foreach (var path in routes.Append(GhostLapSoloWire.ChildrenRoute))
            { using var denied = await host.GetAsync(path, cookie, ct); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode); }
            await AssertRevokedAsync(revoked[0], () => revoked[0].InvokeAsync<GhostLapReply<ChallengePrepared>>("PrepareFork", new PrepareForkRequest(source, 1140), ct), ct);
            await AssertRevokedAsync(revoked[1], () => revoked[1].InvokeAsync<GhostLapReply<ChallengeFinishView>>("FinishFork", key, ct), ct);
        }
        finally { foreach (var hub in revoked) await hub.DisposeAsync(); }
        output.WriteLine("SECURITY foreign=unknown 404; no catalog disclosure; invalid/tampered/revoked cookies 401; revoked Fork hub methods refused; unfinished/corrupt/unsupported source refused; runtime restricted login sands_realtime_solo.");
    }
}
