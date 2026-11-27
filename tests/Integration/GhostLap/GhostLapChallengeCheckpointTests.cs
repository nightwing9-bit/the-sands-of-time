extern alias realtime;

using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Channels;
using GhostLap.Contracts.Multiplayer;
using GhostLap.Contracts.Solo;
using GhostLap.Simulation;
using GhostLap.Solo;
using GhostLap.Tests.Simulation;
using GhostLap.Timeline;
using Integration.Tests.Support;
using Marrow.Infrastructure.Identity;
using Marrow.Infrastructure.Identity.Policy;
using Marrow.Infrastructure.Timeline;
using Marrow.Timeline;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using realtime::GhostLapHost;

namespace Integration.Tests.GhostLap;

public sealed partial class GhostLapSoloCheckpointTests
{
    [Fact]
    public async Task Full_challenge_with_concurrent_preparation_survives_lost_reply_and_cold_restart()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = timeout.Token;
        var secret = IdentityApiFactory.NewMasterSecret();
        await using var api = new IdentityApiFactory(db, secret);
        var owner = api.NewBrowser();
        var subject = (await owner.PostAsync("/auth/guest/start")).Str("subject");
        var cookie = owner.Cookies[Flows.GuestCookie];
        var foreign = api.NewBrowser();
        var otherSubject = (await foreign.PostAsync("/auth/guest/start")).Str("subject");
        var otherCookie = foreign.Cookies[Flows.GuestCookie];
        await db.ScalarAsync<object>("CREATE EXTENSION IF NOT EXISTS pg_stat_statements");
        await using var provider = ChallengeServices();
        var engine = provider.GetRequiredService<ITimelineEngine<GameState, GameInput, GameContext>>();
        var store = new SoloOriginalStore(provider.GetRequiredService<NpgsqlDataSource>());
        var authorizer = new OwnerResourceAuthorizer(new SoloOwnershipLookup(store));
        var originals = new SoloOriginals(engine, store, authorizer);
        var branches = new SoloBranches(engine, store, authorizer);
        var actor = GuestActor(subject);
        // The isolated database has no saved sources. Make two focused canonical fixtures, without extra real-time rounds.
        var source = await ChallengeSourceAsync(originals, actor, 42, ct);
        var otherSource = await ChallengeSourceAsync(originals, GuestActor(otherSubject), 43, ct);
        var fingerprint = await ChallengeFingerprintAsync(source.Original.Timeline.Value);
        var key = "live-challenge-" + Guid.NewGuid().ToString("N");
        ChallengeResultView result;
        ChallengeStarted started;
        SoloFrame final;
        var framesSeen = new List<(long Tick, double Ms)>();
        int originalPid;
        await using (var host = await Kestrel.StartAsync(db, secret, db.RealtimeSoloConnectionString, ct))
        {
            originalPid = host.Pid;
            await using var hub = await host.ConnectAsync(cookie, ct);
            await using var other = await host.ConnectAsync(otherCookie, ct);
            var denied = await other.InvokeAsync<GhostLapReply<ChallengePrepared>>("PrepareChallenge", new PrepareChallengeRequest(source.Original.Timeline.Value.ToString("D")), ct);
            var unknown = await other.InvokeAsync<GhostLapReply<ChallengePrepared>>("PrepareChallenge", new PrepareChallengeRequest(Guid.NewGuid().ToString("D")), ct);
            Assert.Equal(SoloFailure.NotFound, denied.Code);
            Assert.Equal(denied, unknown);
            Assert.Equal(ChallengeFailure.NotReady, (await hub.InvokeAsync<GhostLapReply<ChallengeStarted>>("StartChallenge", new StartChallengeRequest(key), ct)).Code);
            var before = host.Resources();
            var readsBefore = await ChallengeReadStatsAsync();
            var selectsBefore = await ChallengeSelectStatsAsync();
            var prep = Stopwatch.StartNew();
            var ready = Value(await hub.InvokeAsync<GhostLapReply<ChallengePrepared>>("PrepareChallenge", new PrepareChallengeRequest(source.Original.Timeline.Value.ToString("D")), ct));
            prep.Stop();
            var after = host.Resources();
            var readsAfter = await ChallengeReadStatsAsync();
            Assert.Equal(("Ready", source.Original.FinalStateHash, "ghost-v1", "arena-v1", 2700L),
                (ready.Phase, ready.FinalStateHash, ready.RulesVersion, ready.LayoutVersion, ready.FinalTick));
            output.WriteLine($"PREP A {prep.Elapsed.TotalSeconds:F3}s CPU {after.CpuMs-before.CpuMs:F1}ms RSS {before.Rss}->{after.Rss}; history checkpoint reads {readsAfter-readsBefore}; SELECTs {await ChallengeSelectStatsAsync()-selectsBefore}");

            var watch = Stopwatch.StartNew();
            var frames = Channel.CreateUnbounded<(ChallengeFrameView Frame, double Ms)>();
            var ended = new TaskCompletionSource<SoloRunEnded>(TaskCreationOptions.RunContinuationsAsynchronously);
            hub.On<ChallengeFrameView>("ChallengeFrame", f => frames.Writer.TryWrite((f, watch.Elapsed.TotalMilliseconds)));
            hub.On<SoloRunEnded>("ChallengeEnded", e => ended.TrySetResult(e));
            started = Value(await hub.InvokeAsync<GhostLapReply<ChallengeStarted>>("StartChallenge", new StartChallengeRequest(key), ct));
            Assert.Equal((0L, 0L, source.Original.Timeline.Value.ToString("D"), 42L),
                (started.Living.Tick, started.Ghost.Tick, started.OriginalId, started.Seed));
            AssertGhost(started.Ghost, source.States[0]);
            Assert.Equal(ChallengeFailure.NotReady, (await hub.InvokeAsync<GhostLapReply<ChallengeStarted>>("StartChallenge", new StartChallengeRequest(key), ct)).Code);
            // Caller identity is entirely server-derived. Start/stream/cancel have no caller-supplied challenge/owner field.
            Assert.Equal(ChallengeFailure.NotReady, (await other.InvokeAsync<GhostLapReply<ChallengeStarted>>("StartChallenge", new StartChallengeRequest(key), ct)).Code);
            Assert.False(Value(await other.InvokeAsync<GhostLapReply<bool>>("CancelChallenge", ct)));
            var empty = Channel.CreateUnbounded<SoloInputItem>(); empty.Writer.Complete();
            Assert.Equal(SoloFailure.NoRun, (await other.InvokeAsync<GhostLapReply<InputStreamSummary>>("StreamChallengeInputs", empty.Reader, ct)).Code);
            Assert.Equal(SoloFailure.NotFound, Value(await other.InvokeAsync<GhostLapReply<ChallengeFinishView>>("FinishChallenge", key, ct)).Code);
            Assert.Equal(ChallengeFailure.NotComplete, Value(await hub.InvokeAsync<GhostLapReply<ChallengeFinishView>>("FinishChallenge", key, ct)).Code);
            var replacement = await hub.InvokeAsync<GhostLapReply<ChallengePrepared>>("PrepareChallenge", new PrepareChallengeRequest(otherSource.Original.Timeline.Value.ToString("D")), ct);
            Assert.Equal(ChallengeFailure.ChallengeActive, replacement.Code);

            var inputs = Channel.CreateUnbounded<SoloInputItem>();
            var stream = hub.InvokeAsync<GhostLapReply<InputStreamSummary>>("StreamChallengeInputs", inputs.Reader, ct);
            Task? concurrent = null;
            double prepStart = 0, prepEnd = 0;
            long? baselineReads = null;
            long? quietReads = null;
            long baselineTick = 0, quietTick = 0;
            int sent = 0;
            var visited = new HashSet<string>();
            SoloFrame previous = started.Living;
            var detour = 0;
            var lastTick = 0L;
            while (true)
            {
                var (frame, at) = await frames.Reader.ReadAsync(ct);
                Assert.True(frame.Tick > lastTick);
                Assert.InRange(frame.Tick, 1, 2700);
                lastTick = frame.Tick;
                framesSeen.Add((frame.Tick, at));
                Assert.Equal(frame.Tick, frame.Living.Tick);
                Assert.Equal(frame.Tick, frame.Ghost.Tick);
                AssertGhost(frame.Ghost, source.States[(int)frame.Tick]);
                Assert.Equal(frame.Living.TotalScore-frame.Ghost.TotalScore, frame.Delta);
                if (frame.Tick >= 120 && baselineReads is null) { baselineReads = await ChallengeReadStatsAsync(); baselineTick = frame.Tick; }
                if (frame.Tick >= 450 && concurrent is null)
                {
                    Assert.InRange((await ChallengeReadStatsAsync())-baselineReads!.Value-(frame.Tick-baselineTick), -3L, 3L);
                    prepStart = at;
                    concurrent = Task.Run(async () =>
                    {
                        var r0 = host.Resources();
                        var sql0 = await ChallengeReadStatsAsync();
                        var selects0 = await ChallengeSelectStatsAsync();
                        var b = Value(await other.InvokeAsync<GhostLapReply<ChallengePrepared>>("PrepareChallenge", new PrepareChallengeRequest(otherSource.Original.Timeline.Value.ToString("D")), ct));
                        prepEnd = watch.Elapsed.TotalMilliseconds;
                        var r1 = host.Resources();
                        Assert.Equal(otherSource.Original.FinalStateHash, b.FinalStateHash);
                        output.WriteLine($"PREP B {(prepEnd-prepStart)/1000:F3}s CPU {r1.CpuMs-r0.CpuMs:F1}ms RSS {r0.Rss}->{r1.Rss}; history checkpoint reads {await ChallengeReadStatsAsync()-sql0}; SELECTs including living recording {await ChallengeSelectStatsAsync()-selects0}");
                        Assert.True(Value(await other.InvokeAsync<GhostLapReply<bool>>("CancelChallenge", ct)));
                        Assert.Equal(ChallengeFailure.NotReady, (await other.InvokeAsync<GhostLapReply<ChallengeStarted>>("StartChallenge", new StartChallengeRequest("cancelled"), ct)).Code);
                    }, ct);
                }
                if (frame.Tick >= 1800 && quietReads is null)
                {
                    Assert.NotNull(concurrent); await concurrent;
                    quietReads = await ChallengeReadStatsAsync(); quietTick = frame.Tick;
                }
                if (frame.Tick >= 2400 && frame.Tick < 2450 && quietReads is not null)
                    Assert.InRange((await ChallengeReadStatsAsync())-quietReads.Value-(frame.Tick-quietTick), -3L, 3L);
                if (frame.Final) { final = frame.Living; break; }
                var live = frame.Living;
                var active = source.Context.Plan.ActiveAt(live.Tick).ToArray();
                if (live.Charge > previous.Charge && active.Length > 0) visited.Add(active.OrderBy(c => Distance(c, live)).First().Id);
                var target = active.Where(c => !visited.Contains(c.Id)).OrderBy(c => Distance(c, live)).FirstOrDefault();
                if (live.XFixed == previous.XFixed && live.YFixed == previous.YFixed && detour == 0) detour = 18;
                var dx = target is null ? 0 : target.Site.X-live.XFixed;
                var dy = target is null ? 0 : target.Site.Y-live.YFixed;
                if (detour > 0) { detour--; (dx, dy) = (live.Tick/23%2 == 0 ? 1 : -1, live.Tick/41%2 == 0 ? 1 : -1); }
                await inputs.Writer.WriteAsync(new SoloInputItem(++sent, live.Tick+1, (sbyte)Math.Sign(dx), (sbyte)Math.Sign(dy), live.Charge == 4 && live.Tick%97 == 0), ct);
                previous = live;
            }
            inputs.Writer.Complete();
            var ingress = Value(await stream);
            Assert.Equal(sent, ingress.Received);
            Assert.Equal("final", (await ended.Task.WaitAsync(ct)).Reason);
            Assert.NotNull(concurrent); await concurrent;
            Assert.InRange(watch.Elapsed.TotalSeconds, 89, 115);
            result = (await WaitChallengeFinishAsync(hub, key, ct)).Result!;
            Assert.NotNull(result);
            Assert.Equal(("Challenge", 0L, 2700L, final.TotalScore, source.Original.FinalStateHash),
                (result.Kind, result.ForkTick, result.FinalTick, result.LivingScore, result.SourceFinalStateHash));
            Assert.NotEqual(result.LivingScore, result.SourceScore);
            Assert.Equal(result, (await WaitChallengeFinishAsync(hub, key, ct)).Result);
            await host.DropFinishReplyAsync(cookie, key, ct, "FinishChallenge", "Completed");
            Assert.Equal(result, (await WaitChallengeFinishAsync(hub, key, ct)).Result);
            Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM ghostlap.solo_branches WHERE owner_subject_id=@owner AND operation_key=@key", ("owner", subject), ("key", "challenge."+key)));
            PrintGaps("BASELINE", framesSeen.Where(f => f.Ms >= 4000 && f.Ms < prepStart).ToArray());
            PrintGaps("CONCURRENT", framesSeen.Where(f => f.Ms >= prepStart && f.Ms <= prepEnd).ToArray());
            PrintGaps("FULL", framesSeen.ToArray());
            output.WriteLine($"LIVE pid {originalPid} duration {framesSeen[^1].Ms/1000:F3}s frames {framesSeen.Count} tick {final.Tick} inputs {ingress.Received} rejected {ingress.Rejected} score {final.TotalScore}; operation {key}; source {started.OriginalId}; source hash {result.SourceFinalStateHash}; source record {result.SourceLastRecordHash}; child {result.ChallengeId}; child hash {result.ChildFinalStateHash}; child record {result.ChildLastRecordHash}");
            host.AssertLogHygiene(db.RuntimePasswords.Append(secret).Append(cookie).Append(otherCookie));
        }
        Assert.Equal(fingerprint, await ChallengeFingerprintAsync(source.Original.Timeline.Value));
        var resumed = await owner.PostAsync("/auth/guest/start", csrfCookie: Flows.GuestCsrf);
        Assert.True(resumed.Json.GetProperty("resumed").GetBoolean());
        Assert.Equal(subject, resumed.Str("subject"));
        await using (var cold = await Kestrel.StartAsync(db, secret, db.RealtimeSoloConnectionString, ct))
        {
            Assert.NotEqual(originalPid, cold.Pid);
            await using var hub = await cold.ConnectAsync(cookie, ct);
            Assert.Equal(result, (await WaitChallengeFinishAsync(hub, key, ct)).Result);
            await using var other = await cold.ConnectAsync(otherCookie, ct);
            Assert.Equal(SoloFailure.NotFound, Value(await other.InvokeAsync<GhostLapReply<ChallengeFinishView>>("FinishChallenge", key, ct)).Code);
            await using var coldServices = ChallengeServices();
            var coldEngine = coldServices.GetRequiredService<ITimelineEngine<GameState, GameInput, GameContext>>();
            var replay = (await coldEngine.ReplayAsync(new TimelineId(Guid.Parse(result.ChallengeId)), ct)).Value;
            Assert.Equal((HistoryStatus.Verified, 2700L, result.ChildFinalStateHash, result.ChildLastRecordHash),
                (replay.Status, replay.FinalPosition, replay.FinalStateHash, replay.LastRecordHash));
            var p = replay.FinalState.Players[0];
            Assert.Equal((final.XFixed, final.YFixed, final.Charge, final.OverlitDeadlineTick, final.TotalScore), (p.XFixed, p.YFixed, p.Charge, p.OverlitDeadlineTick, p.TotalScore));
            Assert.True(p.Pickups > 0 && p.DistanceFixed > 0);
            var row = (await store.FindBranchAsync(new TimelineId(Guid.Parse(result.ChallengeId)), ct)).Value;
            Assert.Equal((source.Original.Timeline, 0L, SoloBranchKind.Challenge, SoloOriginalStatus.Complete), (row.Source, row.ForkTick, SoloBranchKinds.Of(row), row.Status));
            Assert.Equal(2700L, await db.ScalarAsync<long>("SELECT count(*) FROM marrow_timeline.steps WHERE timeline_id=@id", ("id", Guid.Parse(result.ChallengeId))));
            Assert.Equal(fingerprint, await ChallengeFingerprintAsync(source.Original.Timeline.Value));
            output.WriteLine($"COLD pid {cold.Pid}: verified all 2700 transitions, same hashes; pickups {p.Pickups} bursts {p.Bursts}; parent {row.Source.Value} fork {row.ForkTick} classification {SoloBranchKinds.Of(row)}; rules {row.RulesVersion}/{row.LayoutVersion}; original persisted fingerprint unchanged");
            foreach (var badCookie in new[] { "invalid" })
                await Assert.ThrowsAnyAsync<Exception>(async () => { await using var deniedHub = await cold.ConnectAsync(badCookie, ct); });
            await using var revoked = await cold.ConnectAsync(cookie, ct);
            Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync("/auth/guest/signout", csrfCookie: Flows.GuestCsrf)).Status);
            await AssertRevokedAsync(revoked, () => revoked.InvokeAsync<GhostLapReply<ChallengePrepared>>("PrepareChallenge", new PrepareChallengeRequest(started.OriginalId), ct), ct);
            output.WriteLine("AUTH: foreign/unknown prepare equal NotFound; foreign start/stream/finish/cancel denied; invalid cookie WebSocket rejected; revoked existing connection cannot prepare; original guest resume preserved owner.");
        }
        var unreachable = new NpgsqlConnectionStringBuilder(db.RealtimeSoloConnectionString) { Port = 1, Timeout = 1 }.ConnectionString;
        await using var failing = await Kestrel.StartAsync(db, secret, unreachable, ct);
        await using var validForeign = await failing.ConnectAsync(otherCookie, ct);
        Assert.Equal(new ChallengeFinishView("Pending", null, SoloFailure.Unavailable), Value(await validForeign.InvokeAsync<GhostLapReply<ChallengeFinishView>>("FinishChallenge", key, ct)));
    }

    [Fact]
    public async Task Challenge_short_host_cancellation_disconnect_and_revocation_boundaries()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = timeout.Token;
        var secret = IdentityApiFactory.NewMasterSecret();
        await using var api = new IdentityApiFactory(db, secret);
        var owner = api.NewBrowser();
        var subject = (await owner.PostAsync("/auth/guest/start")).Str("subject");
        var cookie = owner.Cookies[Flows.GuestCookie];
        await using var services = ChallengeServices();
        var engine = services.GetRequiredService<ITimelineEngine<GameState, GameInput, GameContext>>();
        var store = new SoloOriginalStore(services.GetRequiredService<NpgsqlDataSource>());
        var originals = new SoloOriginals(engine, store, new OwnerResourceAuthorizer(new SoloOwnershipLookup(store)));
        var source = await ChallengeSourceAsync(originals, GuestActor(subject), 42, ct);
        var sourceId = source.Original.Timeline.Value.ToString("D");
        await using var host = await Kestrel.StartAsync(db, secret, db.RealtimeSoloConnectionString, ct);
        await using var hub = await host.ConnectAsync(cookie, ct);
        var key = "short-cancel-"+Guid.NewGuid().ToString("N");
        Value(await hub.InvokeAsync<GhostLapReply<ChallengePrepared>>("PrepareChallenge", new PrepareChallengeRequest(sourceId), ct));
        // Replacement of an unstarted selection reuses the verified buffer, without corrupting its identity.
        var repeat = Value(await hub.InvokeAsync<GhostLapReply<ChallengePrepared>>("PrepareChallenge", new PrepareChallengeRequest(sourceId), ct));
        Assert.Equal(source.Original.FinalStateHash, repeat.FinalStateHash);
        var frame = new TaskCompletionSource<ChallengeFrameView>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = hub.On<ChallengeFrameView>("ChallengeFrame", f => frame.TrySetResult(f));
        var started = Value(await hub.InvokeAsync<GhostLapReply<ChallengeStarted>>("StartChallenge", new StartChallengeRequest(key), ct));
        await frame.Task.WaitAsync(ct);
        Assert.True(Value(await hub.InvokeAsync<GhostLapReply<bool>>("CancelChallenge", ct)));
        Assert.False(Value(await hub.InvokeAsync<GhostLapReply<bool>>("CancelChallenge", ct)));
        Assert.Equal(ChallengeFailure.NotComplete, Value(await hub.InvokeAsync<GhostLapReply<ChallengeFinishView>>("FinishChallenge", key, ct)).Code);
        var cancelled = (await store.FindBranchAsync(new TimelineId(Guid.Parse(started.ChallengeId)), ct)).Value;
        Assert.Equal(SoloOriginalStatus.Recording, cancelled.Status);
        Assert.Null(cancelled.FinalStateHash);
        Assert.Equal(ChallengeFailure.NotReady, (await hub.InvokeAsync<GhostLapReply<ChallengeStarted>>("StartChallenge", new StartChallengeRequest(key), ct)).Code);
        Value(await hub.InvokeAsync<GhostLapReply<ChallengePrepared>>("PrepareChallenge", new PrepareChallengeRequest(sourceId), ct));
        var disconnected = Value(await hub.InvokeAsync<GhostLapReply<ChallengeStarted>>("StartChallenge", new StartChallengeRequest(key+"-disconnect"), ct));
        await hub.StopAsync(ct);
        // Reconnect to observe the durable unfinished row, never a false Completed child.
        await using var next = await host.ConnectAsync(cookie, ct);
        Assert.Equal(ChallengeFailure.NotComplete, Value(await next.InvokeAsync<GhostLapReply<ChallengeFinishView>>("FinishChallenge", key+"-disconnect", ct)).Code);
        Assert.Equal(SoloOriginalStatus.Recording, (await store.FindBranchAsync(new TimelineId(Guid.Parse(disconnected.ChallengeId)), ct)).Value.Status);
        // Each revoked entry point uses an independently pre-existing authenticated connection.
        var revoked = new List<HubConnection>();
        try
        {
            for (var i=0; i<5; i++) revoked.Add(await host.ConnectAsync(cookie, ct));
            Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync("/auth/guest/signout", csrfCookie: Flows.GuestCsrf)).Status);
            await AssertRevokedAsync(revoked[0], () => revoked[0].InvokeAsync<GhostLapReply<ChallengePrepared>>("PrepareChallenge", new PrepareChallengeRequest(sourceId), ct), ct);
            await AssertRevokedAsync(revoked[1], () => revoked[1].InvokeAsync<GhostLapReply<ChallengeStarted>>("StartChallenge", new StartChallengeRequest(key), ct), ct);
            var inputs = Channel.CreateUnbounded<SoloInputItem>(); inputs.Writer.Complete();
            await AssertRevokedAsync(revoked[2], () => revoked[2].InvokeAsync<GhostLapReply<InputStreamSummary>>("StreamChallengeInputs", inputs.Reader, ct), ct);
            await AssertRevokedAsync(revoked[3], () => revoked[3].InvokeAsync<GhostLapReply<ChallengeFinishView>>("FinishChallenge", key, ct), ct);
            await AssertRevokedAsync(revoked[4], () => revoked[4].InvokeAsync<GhostLapReply<bool>>("CancelChallenge", ct), ct);
        }
        finally { foreach (var connection in revoked) await connection.DisposeAsync(); }
        output.WriteLine("SHORT HOST: repeated preparation, cancellation after a committed frame, double cancellation, disconnect, no false completed child, and all five revoked-session entry points passed.");
    }

    private ServiceProvider ChallengeServices()
    {
        var s = new ServiceCollection();
        s.AddSingleton(_ => NpgsqlDataSource.Create(db.RealtimeSoloConnectionString));
        s.AddMarrowTimeline(GhostLapTimelineVersions.All, GhostLapTimelineVersions.CheckpointInterval);
        return s.BuildServiceProvider();
    }

    private static Marrow.Kernel.Security.IActorContext GuestActor(string subject) => ClaimsActorContext.From(new ClaimsPrincipal(new ClaimsIdentity([
        new Claim(IdentityClaimTypes.Kind, IdentityClaimTypes.KindGuest), new Claim(IdentityClaimTypes.Subject, subject)], "guest-fixture")));

    private static async Task<(SoloOriginal Original, List<GameState> States, GameContext Context)> ChallengeSourceAsync(SoloOriginals originals, Marrow.Kernel.Security.IActorContext actor, long seed, CancellationToken ct)
    {
        var rec = (await originals.StartAsync(actor, seed, 2, "challenge-source-"+Guid.NewGuid().ToString("N"), ct)).Value;
        var driver = new CandleSeekingDriver(1);
        var states = new List<GameState> { rec.State };
        while (!rec.State.IsFinal)
        {
            foreach (var input in driver.Inputs(rec.State, rec.Context))
                Assert.True(rec.Submit(new SubmittedInput(input.Slot, input.Sequence, input.Tick, input.MoveX, input.MoveY, input.RequestBurst)).IsSuccess);
            states.Add((await rec.CommitTickAsync(ct)).Value.State);
        }
        return ((await rec.CompleteAsync(ct)).Value, states, rec.Context);
    }

    private static void AssertGhost(SoloGhostFrame ghost, GameState state)
    {
        var p = state.Players[0];
        var canonical = new Marrow.Kernel.Canonical.CanonicalWriter().WriteString(GhostLapTimelineVersions.StateSchemaVersion)
            .WriteBytes(GameStateCodec.Instance.EncodeCanonical(state)).ToArray();
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(canonical)), ghost.StateHash);
        Assert.Equal((state.Tick, p.XFixed, p.YFixed, p.Charge, p.IsOverlit, p.OverlitDeadlineTick, p.TotalScore),
            (ghost.Tick, ghost.XFixed, ghost.YFixed, ghost.Charge, ghost.Overlit, ghost.OverlitDeadlineTick, ghost.TotalScore));
        Assert.Equal(state.ActiveCandleIds.ToArray(), ghost.ActiveCandleIds);
        Assert.Equal(state.Events.Select(e => new GhostEventView(e.Kind.ToString(), e.Slot, e.OtherSlot, e.Value, e.CandleId)).ToArray(), ghost.Events);
    }

    private async Task<long> ChallengeSelectStatsAsync() => await db.ScalarAsync<long>("SELECT COALESCE(sum(calls),0)::bigint FROM pg_stat_statements WHERE ltrim(query) ILIKE 'SELECT%' AND userid=(SELECT oid FROM pg_roles WHERE rolname='sands_realtime_solo')");

    private async Task<long> ChallengeReadStatsAsync() => await db.ScalarAsync<long>("SELECT COALESCE(sum(calls),0)::bigint FROM pg_stat_statements WHERE query LIKE 'SELECT position, state_schema_version, state_hash, state_bytes%' AND userid=(SELECT oid FROM pg_roles WHERE rolname='sands_realtime_solo')");

    private async Task<string> ChallengeFingerprintAsync(Guid id) => (await db.ScalarAsync<string>("""
        SELECT md5(row_to_json(h)::text) || '|' ||
          (SELECT md5(string_agg(row_to_json(s)::text, ',' ORDER BY position)) FROM marrow_timeline.steps s WHERE timeline_id=@id) || '|' ||
          (SELECT md5(string_agg(row_to_json(c)::text, ',' ORDER BY position)) FROM marrow_timeline.checkpoints c WHERE timeline_id=@id) || '|' ||
          (SELECT md5(string_agg(row_to_json(i)::text, ',' ORDER BY position,input_index)) FROM marrow_timeline.step_inputs i WHERE timeline_id=@id) || '|' ||
          (SELECT md5(row_to_json(o)::text) FROM ghostlap.solo_originals o WHERE timeline_id=@id)
        FROM marrow_timeline.histories h WHERE timeline_id=@id
        """, ("id", id)))!;

    private static async Task<ChallengeFinishView> WaitChallengeFinishAsync(HubConnection hub, string key, CancellationToken ct)
    {
        for (var i=0; ; i++)
        {
            var result = Value(await hub.InvokeAsync<GhostLapReply<ChallengeFinishView>>("FinishChallenge", key, ct));
            if (result.Status == "Completed") return result;
            Assert.True(i < 60, JsonSerializer.Serialize(result));
            await Task.Delay(250, ct);
        }
    }

    private void PrintGaps(string name, (long Tick, double Ms)[] frames)
    {
        var gaps = frames.Zip(frames.Skip(1), (a,b) => b.Ms-a.Ms).Order().ToArray();
        Assert.NotEmpty(gaps);
        double P(double q) => gaps[(int)((gaps.Length-1)*q)];
        output.WriteLine($"{name} samples {gaps.Length}: delivery gap ms p50 {P(.5):F2} p95 {P(.95):F2} p99 {P(.99):F2} max {gaps[^1]:F2}; ticks {frames[0].Tick}-{frames[^1].Tick}");
    }
}
