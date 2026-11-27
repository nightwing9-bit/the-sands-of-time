extern alias realtime;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Channels;
using GhostLap.Contracts.Multiplayer;
using GhostLap.Contracts.Solo;
using GhostLap.Rules;
using GhostLap.Simulation;
using GhostLap.Solo;
using GhostLap.Tests.Simulation;
using Integration.Tests.Support;
using Marrow.Infrastructure.Identity;
using Marrow.Kernel.Security;
using Marrow.Timeline;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using realtime::GhostLapHost;

namespace Integration.Tests.GhostLap;

/// <summary>
/// Task 11.5 focused host checks over the REAL Hosts.Realtime composition, real guest cookies issued by the real Hosts.Api and the real
/// <c>sands_realtime_solo</c> login. The source original (2,700 ticks) and a one-tick Fork child are recorded through the host's own solo
/// services without waiting for real time. Not covered here (delegated to Codex): a real-time T1140 to T2700 Fork over Kestrel, cold
/// restart of the serving host, revoked-session behavior on the new routes and live-room coexistence.
/// </summary>
[Collection(IdentityCollection.Name)]
[Trait("Category", "Integration")]
public sealed class GhostLapSoloHistoryHostTests(IdentityDatabase db)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const string Originals = GhostLapSoloWire.OriginalsRoute;
    private const string Children = GhostLapSoloWire.ChildrenRoute;

    private static IActorContext GuestActor(string subject) => ClaimsActorContext.From(new ClaimsPrincipal(new ClaimsIdentity(
        [new Claim(IdentityClaimTypes.Kind, IdentityClaimTypes.KindGuest), new Claim(IdentityClaimTypes.Subject, subject)], "guest-fixture")));

    private static async Task<SoloOriginal> RecordOriginalAsync(SoloOriginals originals, IActorContext actor, string key)
    {
        var recording = (await originals.StartAsync(actor, 42, 2, key, CancellationToken.None)).Value;
        var driver = new CandleSeekingDriver(1);
        while (!recording.State.IsFinal)
        {
            foreach (var g in driver.Inputs(recording.State, recording.Context))
            {
                Assert.True(recording.Submit(new SubmittedInput(g.Slot, g.Sequence, g.Tick, g.MoveX, g.MoveY, g.RequestBurst)).IsSuccess);
            }

            Assert.True((await recording.CommitTickAsync(CancellationToken.None)).IsSuccess);
        }

        return (await recording.CompleteAsync(CancellationToken.None)).Value;
    }

    /// <summary>A completed Fork at tick 2699: one real transition through the host's own services.</summary>
    private static async Task<SoloBranchResult> CompleteLastTickForkAsync(SoloBranches branches, IActorContext actor, TimelineId original, string key)
    {
        var fork = (await branches.ForkAsync(actor, original, 2699, SoloBranchKinds.ForkKey(key), CancellationToken.None)).Value;
        var input = GameInput.Neutral(2700, 0, fork.State.Players[0].LastSequence + 1) with { MoveX = 1 };
        Assert.True(fork.Submit(new SubmittedInput(0, input.Sequence, input.Tick, input.MoveX, input.MoveY, false)).IsSuccess);
        Assert.True((await fork.CommitTickAsync(CancellationToken.None)).IsSuccess);
        return (await fork.CompleteAsync(CancellationToken.None)).Value;
    }

    private static Task<HttpResponseMessage> Get(HttpClient http, string? cookie, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (cookie is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", $"{Flows.GuestCookie}={cookie}");
        }

        return http.SendAsync(request);
    }

    private static async Task<T> Json<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(Web))!;
    }

    private static string Body(HttpResponseMessage response) => response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

    private static void AssertCandles(CandlePlan plan, long tick, string[] activeIds, SoloVisibleCandle[]? visible)
    {
        Assert.NotNull(visible);
        Assert.Equal(activeIds.Order(), visible.Where(c => c.Phase == "active").Select(c => c.Id).Order());
        Assert.Equal(plan.Candles.Where(c => c.TelegraphTick <= tick && tick < c.ActivateTick).Select(c => c.Id),
            visible.Where(c => c.Phase == "telegraph").Select(c => c.Id));
        Assert.All(visible, c =>
        {
            var spec = Assert.Single(plan.Candles, site => site.Id == c.Id);
            Assert.Equal((spec.Site.X / (double)GameRulesV1.FixedOne, spec.Site.Y / (double)GameRulesV1.FixedOne), (c.X, c.Y));
        });
    }

    [Fact]
    public async Task Owner_routes_resolve_in_the_real_host_and_serve_exact_history_child_catalog_and_same_tick_comparison()
    {
        var secret = IdentityApiFactory.NewMasterSecret();
        await using var api = new IdentityApiFactory(db, secret);
        await using var host = new RealtimeHostFactory(db, secret, new Dictionary<string, string?> { ["ConnectionStrings:GhostLapSolo"] = db.RealtimeSoloConnectionString });
        var owner = api.NewBrowser();
        var subject = (await owner.PostAsync("/auth/guest/start")).Str("subject");
        var cookie = owner.Cookies[Flows.GuestCookie];
        var foreign = api.NewBrowser();
        await foreign.PostAsync("/auth/guest/start");
        var otherCookie = foreign.Cookies[Flows.GuestCookie];

        var actor = GuestActor(subject);
        var originals = host.Services.GetRequiredService<SoloOriginals>();
        var branches = host.Services.GetRequiredService<SoloBranches>();
        var source = await RecordOriginalAsync(originals, actor, "history-source-" + Guid.NewGuid().ToString("N"));
        var sourceId = source.Timeline.Value.ToString("D");
        var child = await CompleteLastTickForkAsync(branches, actor, source.Timeline, "route-last");
        var childId = child.Timeline.Value.ToString("D");
        // An unfinished fork exists for the owner too; it is neither a completed child nor readable as one.
        var pending = (await branches.ForkAsync(actor, source.Timeline, 100, SoloBranchKinds.ForkKey("route-pending"), CancellationToken.None)).Value;
        var pendingId = pending.Timeline.Value.ToString("D");

        using var http = host.CreateClient();

        // ---- trace: bounded, ordered chunks with a stable pinned source identity ----
        var first = await Json<SoloTraceView>(await Get(http, cookie, $"{Originals}/{sourceId}/trace?from=0&count=300"));
        Assert.Equal(("solo-history/1", sourceId, source.FinalStateHash, 2700L, 0L, 300, 300L), (first.ContractVersion, first.Source.OriginalId,
            first.Source.FinalStateHash, first.Source.TerminalTick, first.FromTick, first.Count, first.NextTick));
        Assert.Equal(("ghost-lap", "ghost-v1", "ghost-input/1", "ghost-state/1", "ghost-context/1", "ghost-v1", "arena-v1"),
            (first.Source.Versions.DomainId, first.Source.Versions.ReducerVersion, first.Source.Versions.InputSchemaVersion,
                first.Source.Versions.StateSchemaVersion, first.Source.Versions.ContextSchemaVersion, first.Source.Versions.RulesVersion,
                first.Source.Versions.LayoutVersion));
        Assert.Equal(Enumerable.Range(0, 300).Select(t => (long)t), first.Frames.Select(f => f.Tick));
        var middle = await Json<SoloTraceView>(await Get(http, cookie, $"{Originals}/{sourceId}/trace?from=1100&count=100"));
        Assert.Equal((first.Source, 1200L), (middle.Source, middle.NextTick));
        var tail = await Json<SoloTraceView>(await Get(http, cookie, $"{Originals}/{sourceId}/trace?from=2401&count=300&v=solo-history/1"));
        Assert.Equal((first.Source, 300, (long?)null), (tail.Source, tail.Frames.Length, tail.NextTick));
        Assert.True(tail.Frames[^1].Final && tail.Frames[^1].StateHash == source.FinalStateHash);
        Assert.False(tail.Frames[^2].Final);
        Assert.Equal(source.Result.Scores[0].TotalScore, tail.Frames[^1].TotalScore);

        // ---- inspect: T0 / T1140 / T2700, identical to the same tick inside a trace chunk ----
        foreach (var tick in new[] { 0, 1140, 2700 })
        {
            var inspect = await Json<SoloInspectView>(await Get(http, cookie, $"{Originals}/{sourceId}/inspect/{tick}"));
            Assert.Equal(first.Source, inspect.Source);
            Assert.Equal(tick, inspect.Frame.Tick);
            var chunk = tick < 300 ? first : tick < 1200 ? middle : tail;
            Assert.Equal(JsonSerializer.Serialize(chunk.Frames.Single(f => f.Tick == tick)), JsonSerializer.Serialize(inspect.Frame));
        }

        var t1140 = middle.Frames.Single(f => f.Tick == 1140);
        var plan = CandlePlan.Create(source.Seed);
        Assert.Contains(first.Frames, f => f.VisibleCandles!.Any(c => c.Phase == "telegraph"));
        Assert.All(first.Frames.Concat(middle.Frames), f => AssertCandles(plan, f.Tick, f.ActiveCandleIds, f.VisibleCandles));
        Assert.True(t1140.TotalScore < source.Result.Scores[0].TotalScore && t1140.TotalScore > 0, "historical score, not the final score");
        Assert.True(t1140.ActiveCandleIds.Length > 0 && t1140.CollectedCount > 0 && t1140.DistanceFixed > 0);
        Assert.Contains(first.Frames, f => f.Events.Any(e => e.Kind == "CandleCollected" && e.CandleId.Length > 0));

        // ---- malformed or unsupported requests fail closed ----
        foreach (var (path, status, code) in new[]
        {
            ($"{Originals}/{sourceId}/trace?count=301", HttpStatusCode.BadRequest, "invalid_request"),
            ($"{Originals}/{sourceId}/trace?count=0", HttpStatusCode.BadRequest, "invalid_request"),
            ($"{Originals}/{sourceId}/trace?from=-1", HttpStatusCode.BadRequest, "invalid_tick"),
            ($"{Originals}/{sourceId}/trace?from=2701", HttpStatusCode.BadRequest, "invalid_tick"),
            ($"{Originals}/{sourceId}/trace?v=solo-history/2", HttpStatusCode.BadRequest, "contract_unsupported"),
            ($"{Originals}/{sourceId}/inspect/2701", HttpStatusCode.BadRequest, "invalid_tick"),
            ($"{Originals}/{sourceId}/inspect/-1", HttpStatusCode.BadRequest, "invalid_tick"),
            ($"{Originals}/{sourceId}/inspect/abc", HttpStatusCode.BadRequest, "invalid_tick"),
            ($"{Children}?limit=26", HttpStatusCode.BadRequest, "invalid_request"),
            ($"{Children}?after=not-a-guid", HttpStatusCode.BadRequest, "invalid_request"),
            ($"{Children}/{childId}/comparison?count=301", HttpStatusCode.BadRequest, "invalid_request"),
            ($"{Children}/{childId}/comparison?stride=0", HttpStatusCode.BadRequest, "invalid_request"),
        })
        {
            var response = await Get(http, cookie, path);
            Assert.Equal((status, code), (response.StatusCode, JsonDocument.Parse(Body(response)).RootElement.GetProperty("code").GetString()));
        }

        // ---- the child catalog: only completed children, separate from the Originals list ----
        var list = await Json<SoloOriginalView[]>(await Get(http, cookie, Originals));
        Assert.Equal([sourceId], list.Select(o => o.Id));
        var page = await Json<SoloChildPageView>(await Get(http, cookie, $"{Children}?limit=10"));
        var item = Assert.Single(page.Items);
        Assert.Null(page.NextAfter);
        Assert.Equal((childId, sourceId, "Fork", "route-last", 2699L, 2700L, child.FinalStateHash, true, "Completed"),
            (item.Id, item.SourceId, item.Kind, item.OperationKey, item.ForkTick, item.FinalTick, item.FinalStateHash, item.Verified, item.Status));
        Assert.Equal(child.FinalState.Players[0].TotalScore, item.Score);
        Assert.Equal("ghost-v1", item.Versions.ReducerVersion);
        Assert.Single((await Json<SoloChildPageView>(await Get(http, cookie, $"{Children}?source={sourceId}"))).Items);
        Assert.Empty((await Json<SoloChildPageView>(await Get(http, cookie, $"{Children}?source={Guid.NewGuid():D}"))).Items);
        Assert.DoesNotContain(pendingId, JsonSerializer.Serialize(page));

        var detail = await Json<ChallengeResultView>(await Get(http, cookie, $"{Children}/{childId}"));
        Assert.Equal(("Fork", 2699L, source.FinalStateHash, child.FinalStateHash, 42L, (byte)2), (detail.Kind, detail.ForkTick, detail.SourceFinalStateHash,
            detail.ChildFinalStateHash, detail.Seed, detail.MascotId));
        Assert.Equal(detail.LivingScore - detail.SourceScore, detail.Delta);

        // ---- same-tick comparison: equal through the fork tick, signed delta after, true historical source scores ----
        var cmp = await Json<SoloComparisonView>(await Get(http, cookie, $"{Children}/{childId}/comparison?from=2697&count=4&stride=1"));
        Assert.Equal((childId, sourceId, "Fork", 2699L, 2700L, source.FinalStateHash, child.FinalStateHash, 2697L, (long?)null),
            (cmp.ChildId, cmp.SourceId, cmp.Kind, cmp.ForkTick, cmp.FinalTick, cmp.SourceFinalStateHash, cmp.ChildFinalStateHash, cmp.FromTick, cmp.NextTick));
        Assert.Equal([false, false, false, true], cmp.Points.Select(p => p.AfterFork));
        Assert.All(cmp.Points.Take(3), p => Assert.Equal(0, p.Delta));
        Assert.Equal(first.Source.Versions, cmp.Versions);
        Assert.Equal(tail.Frames[^4].TotalScore, cmp.Points[0].SourceScore); // tick 2698 in the trace is the same state
        var end = cmp.Points[3];
        Assert.Equal((source.Result.Scores[0].TotalScore, child.FinalState.Players[0].TotalScore, true), (end.SourceScore, end.ChildScore, end.Final));
        Assert.Equal(end.ChildScore - end.SourceScore, end.Delta);
        var sampled = await Json<SoloComparisonView>(await Get(http, cookie, $"{Children}/{childId}/comparison?from=0&count=300&stride=10"));
        Assert.Equal((271, (long?)null), (sampled.Points.Length, sampled.NextTick));
        Assert.All(sampled.Points.Where(p => !p.AfterFork), p => Assert.Equal(0, p.Delta));

        // ---- privacy: a foreign guest, an anonymous caller and unknown ids learn nothing ----
        var unknown = Guid.NewGuid().ToString("D");
        foreach (var path in new[]
        {
            $"{Originals}/{{0}}", $"{Originals}/{{0}}/trace", $"{Originals}/{{0}}/inspect/0", $"{Children}/{{0}}",
            $"{Children}/{{0}}/comparison", $"{Children}/{{0}}/trace",
        })
        {
            var foreignSource = await Get(http, otherCookie, string.Format(path, path.Contains("/originals/") ? sourceId : childId));
            var unknownId = await Get(http, otherCookie, string.Format(path, unknown));
            Assert.Equal(HttpStatusCode.NotFound, foreignSource.StatusCode);
            Assert.Equal(Body(unknownId), Body(foreignSource));
            Assert.Equal(HttpStatusCode.NotFound, unknownId.StatusCode);
            Assert.NotEqual(HttpStatusCode.OK, (await Get(http, null, string.Format(path, childId))).StatusCode);
        }

        Assert.Empty((await Json<SoloChildPageView>(await Get(http, otherCookie, Children))).Items);
        Assert.Empty(await Json<SoloOriginalView[]>(await Get(http, otherCookie, Originals)));
        // Saved children have a bounded visual-history path after refresh, without entering the Original library.
        var childTrace = await Json<SoloChildTraceView>(await Get(http, cookie, $"{Children}/{childId}/trace?from=2698&count=300"));
        Assert.Equal((childId, sourceId, "Fork", 2699L, child.FinalStateHash, source.FinalStateHash, (long?)null),
            (childTrace.ChildId, childTrace.SourceId, childTrace.Kind, childTrace.ForkTick, childTrace.ChildFinalStateHash, childTrace.SourceFinalStateHash, childTrace.NextTick));
        Assert.Equal(new long[] { 2698, 2699, 2700 }, childTrace.Frames.Select(f => f.Tick));
        Assert.Equal(child.FinalStateHash, childTrace.Frames[^1].StateHash);
        Assert.All(childTrace.Frames, f => AssertCandles(plan, f.Tick, f.ActiveCandleIds, f.VisibleCandles));
        var childPrefix = await Json<SoloChildTraceView>(await Get(http, cookie, $"{Children}/{childId}/trace?from=0&count=2"));
        Assert.Equal(JsonSerializer.Serialize(first.Frames.Take(2)), JsonSerializer.Serialize(childPrefix.Frames));
        var originalTail = await Json<SoloTraceView>(await Get(http, cookie, $"{Originals}/{sourceId}/trace?from=2698&count=2"));
        Assert.Equal(JsonSerializer.Serialize(originalTail.Frames), JsonSerializer.Serialize(childTrace.Frames.Take(2)));
        foreach (var (query, status) in new[] { ("count=0", HttpStatusCode.BadRequest), ("count=301", HttpStatusCode.BadRequest),
            ("from=-1", HttpStatusCode.BadRequest), ("from=2701", HttpStatusCode.BadRequest), ("v=unknown", HttpStatusCode.BadRequest) })
            Assert.Equal(status, (await Get(http, cookie, $"{Children}/{childId}/trace?{query}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Get(http, otherCookie, $"{Children}/{childId}/trace")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Get(http, cookie, $"{Children}/{pendingId}/trace")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Get(http, cookie, $"{Children}/{sourceId}/trace")).StatusCode);

        // An unfinished child, an original id used as a child id and a child id used as an original id are the same 404.
        Assert.Equal(HttpStatusCode.NotFound, (await Get(http, cookie, $"{Children}/{pendingId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Get(http, cookie, $"{Children}/{pendingId}/comparison")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Get(http, cookie, $"{Children}/{sourceId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Get(http, cookie, $"{Originals}/{childId}/trace")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Get(http, cookie, $"{Originals}/not-a-guid/inspect/0")).StatusCode);
    }

    [Fact]
    public async Task Hub_prepares_and_starts_a_fork_at_T1140_that_accepts_its_first_input_at_T1141_and_classifies_input_refusals()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = timeout.Token;
        var secret = IdentityApiFactory.NewMasterSecret();
        await using var api = new IdentityApiFactory(db, secret);
        await using var host = new RealtimeHostFactory(db, secret, new Dictionary<string, string?> { ["ConnectionStrings:GhostLapSolo"] = db.RealtimeSoloConnectionString });
        var owner = api.NewBrowser();
        var subject = (await owner.PostAsync("/auth/guest/start")).Str("subject");
        var cookie = owner.Cookies[Flows.GuestCookie];
        var foreign = api.NewBrowser();
        await foreign.PostAsync("/auth/guest/start");
        var source = await RecordOriginalAsync(host.Services.GetRequiredService<SoloOriginals>(), GuestActor(subject), "fork-source-" + Guid.NewGuid().ToString("N"));
        var sourceId = source.Timeline.Value.ToString("D");
        var registry = host.Services.GetRequiredService<SoloChallengeRegistry>();

        await using var other = host.ConnectTo(GhostLapSoloWire.HubPath, guestCookie: foreign.Cookies[Flows.GuestCookie]);
        await other.StartAsync(ct);
        var denied = await other.InvokeAsync<GhostLapReply<ChallengePrepared>>(GhostLapChallengeWire.PrepareForkMethod, new PrepareForkRequest(sourceId, 1140), ct);
        var unknown = await other.InvokeAsync<GhostLapReply<ChallengePrepared>>(GhostLapChallengeWire.PrepareForkMethod, new PrepareForkRequest(Guid.NewGuid().ToString("D"), 1140), ct);
        Assert.Equal((false, SoloFailure.NotFound), (denied.Ok, denied.Code));
        Assert.Equal(denied, unknown);
        Assert.Equal(0, registry.Active);

        var frames = Channel.CreateUnbounded<ChallengeFrameView>();
        var rejections = new List<SoloInputRejected>();
        await using var hub = host.ConnectTo(GhostLapSoloWire.HubPath, guestCookie: cookie);
        hub.On<ChallengeFrameView>(GhostLapChallengeWire.ChallengeFrameClientMethod, f => frames.Writer.TryWrite(f));
        hub.On<SoloInputRejected>("InputRejected", r => { lock (rejections) { rejections.Add(r); } });
        await hub.StartAsync(ct);

        // Refused before anything is prepared: final tick (inspect-only), out of range, malformed id; and no start without Ready.
        Assert.Equal("fork_not_allowed", (await hub.InvokeAsync<GhostLapReply<ChallengePrepared>>("PrepareFork", new PrepareForkRequest(sourceId, 2700), ct)).Code);
        Assert.Equal("invalid_tick", (await hub.InvokeAsync<GhostLapReply<ChallengePrepared>>("PrepareFork", new PrepareForkRequest(sourceId, 2701), ct)).Code);
        Assert.Equal("invalid_tick", (await hub.InvokeAsync<GhostLapReply<ChallengePrepared>>("PrepareFork", new PrepareForkRequest(sourceId, -1), ct)).Code);
        Assert.Equal(SoloFailure.InvalidRequest, (await hub.InvokeAsync<GhostLapReply<ChallengePrepared>>("PrepareFork", new PrepareForkRequest("nope", 5), ct)).Code);
        Assert.Equal(ChallengeFailure.NotReady, (await hub.InvokeAsync<GhostLapReply<ChallengeStarted>>("StartChallenge", new StartChallengeRequest("fork-early"), ct)).Code);
        Assert.Equal(0, registry.Active);

        var ready = (await hub.InvokeAsync<GhostLapReply<ChallengePrepared>>("PrepareFork", new PrepareForkRequest(sourceId, 1140), ct)).Value!;
        Assert.Equal((ChallengePhases.Ready, "Fork", 1140L, 1141L, source.FinalStateHash, "ghost-v1"), (ready.Phase, ready.Kind, ready.ForkTick, ready.FirstInputTick, ready.FinalStateHash, ready.Versions!.ReducerVersion));

        var started = (await hub.InvokeAsync<GhostLapReply<ChallengeStarted>>("StartChallenge", new StartChallengeRequest("route-fork"), ct)).Value!;
        Assert.Equal(("Fork", 1140L, 1141L, "fork.route-fork", sourceId, source.FinalStateHash, 42L), (started.Kind, started.ForkTick, started.FirstInputTick,
            started.OperationKey, started.OriginalId, started.SourceFinalStateHash, started.Seed));
        Assert.Equal((1140L, 1140L, started.Ghost.TotalScore, started.Ghost.DistanceFixed), (started.Living.Tick, started.Ghost.Tick, started.Living.TotalScore, started.Living.DistanceFixed));
        Assert.Equal(started.Ghost.ActiveCandleIds, started.Living.ActiveCandleIds);
        var plan = CandlePlan.Create(source.Seed);
        AssertCandles(plan, started.Living.Tick, started.Living.ActiveCandleIds!, started.Living.VisibleCandles);
        Assert.True(started.Living.TotalScore > 0 && started.Living.TotalScore < source.Result.Scores[0].TotalScore);

        // The ghost at T1140 is exactly the owner's inspect of the original at T1140 (full state identity).
        using var http = host.CreateClient();
        var inspect = await Json<SoloInspectView>(await Get(http, cookie, $"{Originals}/{sourceId}/inspect/1140"));
        Assert.Equal(JsonSerializer.Serialize(inspect.Frame), JsonSerializer.Serialize(started.Ghost));
        Assert.Equal(inspect.Frame.VisibleCandles!, started.Living.VisibleCandles!);

        // Input: the first accepted input is for T1141; refusals are classified and a refused Burst edge is always reported.
        var inputs = Channel.CreateUnbounded<SoloInputItem>();
        var summary = hub.InvokeAsync<GhostLapReply<InputStreamSummary>>("StreamChallengeInputs", inputs.Reader, ct);
        var ack = started.Living.AckedInputSeq;
        Assert.True(ack >= 0);
        await inputs.Writer.WriteAsync(new SoloInputItem(ack + 1, 1141, 1, 0, false), ct);
        ChallengeFrameView firstFrame = await frames.Reader.ReadAsync(ct);
        Assert.Equal((1141L, 1141L), (firstFrame.Tick, firstFrame.Ghost.Tick));
        Assert.Equal((firstFrame.Living.TotalScore - firstFrame.Ghost.TotalScore, 1L), (firstFrame.Delta, firstFrame.Seq));
        Assert.NotNull(firstFrame.Pacing);
        Assert.Equal(ack + 1, firstFrame.Living.AckedInputSeq);
        AssertCandles(plan, firstFrame.Tick, firstFrame.Living.ActiveCandleIds!, firstFrame.Living.VisibleCandles);

        ChallengeFrameView latest = firstFrame;
        while (latest.Tick < 1143)
        {
            var next = await frames.Reader.ReadAsync(ct);
            Assert.True(next.Tick > latest.Tick && next.Seq > latest.Seq);
            Assert.Equal(next.Tick, next.Ghost.Tick);
            latest = next;
        }

        // duplicate sequence (benign movement), stale Burst press whose edge the reducer has not seen (burst_rejected), malformed vector (refused)
        // Notices for movement are throttled (about one per 50 ms), a refused Burst press is not; space the movement refusals out.
        await inputs.Writer.WriteAsync(new SoloInputItem(ack + 1, latest.Tick + 1, 1, 0, false), ct);
        await Task.Delay(120, ct);
        await inputs.Writer.WriteAsync(new SoloInputItem(ack + 2, -1000, 0, 0, true), ct);
        await Task.Delay(120, ct);
        await inputs.Writer.WriteAsync(new SoloInputItem(ack + 3, latest.Tick + 1, 5, 0, false), ct);
        var until = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            lock (rejections)
            {
                if (rejections.Count >= 3)
                {
                    break;
                }
            }

            Assert.True(DateTime.UtcNow < until, "expected three classified refusals");
            await Task.Delay(25, ct);
        }

        SoloInputRejected[] seen;
        lock (rejections)
        {
            seen = [.. rejections];
        }

        var duplicate = seen.Single(r => r.Seq == ack + 1);
        var burst = seen.Single(r => r.Seq == ack + 2);
        var malformed = seen.Single(r => r.Seq == ack + 3);
        Assert.Equal(("StaleSequence", InputRejectionClasses.BenignMovement, false), (duplicate.Code, duplicate.Class, duplicate.BurstRequested));
        Assert.Equal(("LateTick", InputRejectionClasses.BurstRejected, true), (burst.Code, burst.Class, burst.BurstRequested));
        Assert.Equal(("InvalidVector", InputRejectionClasses.Refused), (malformed.Code, malformed.Class));
        Assert.All(seen, r => Assert.True(r.NextServerTick >= 1142));

        // Cancel: no result, the stored child stays an unfinished recording and is not a completed child; the Challenge path is unchanged.
        inputs.Writer.Complete();
        Assert.True((await hub.InvokeAsync<GhostLapReply<bool>>("CancelChallenge", ct)).Value);
        Assert.Equal(0, registry.Active);
        var finish = (await hub.InvokeAsync<GhostLapReply<ChallengeFinishView>>("FinishFork", "route-fork", ct)).Value!;
        Assert.Equal((ChallengeFinishStatuses.Failed, ChallengeFailure.NotComplete), (finish.Status, finish.Code));
        Assert.Null(finish.Result);
        Assert.Equal(SoloFailure.NotFound, (await hub.InvokeAsync<GhostLapReply<ChallengeFinishView>>("FinishChallenge", "route-fork", ct)).Value!.Code);
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM ghostlap.solo_branches WHERE operation_key = 'fork.route-fork' AND status = 'Recording'"));
        Assert.Empty((await Json<SoloChildPageView>(await Get(http, cookie, Children))).Items);

        await summary.WaitAsync(ct);
        SoloTraceFrame? armed = null;
        for (var tick = 0; tick < 2700 && armed is null; tick += 300)
        {
            var chunk = await host.Services.GetRequiredService<SoloOriginals>().TraceAsync(GuestActor(subject), source.Timeline, tick, 300, ct);
            Assert.True(chunk.IsSuccess);
            armed = chunk.Value.Frames.FirstOrDefault(f => f.IsOverlit && !f.BurstLatch && f.OverlitDeadlineTick-f.Tick > 15);
        }
        Assert.NotNull(armed);
        Assert.True((await hub.InvokeAsync<GhostLapReply<ChallengePrepared>>("PrepareFork", new PrepareForkRequest(sourceId, armed.Tick), ct)).Ok);
        while (frames.Reader.TryRead(out _)) { }
        var retryStart = (await hub.InvokeAsync<GhostLapReply<ChallengeStarted>>("StartChallenge", new StartChallengeRequest("burst-retry"), ct)).Value!;
        Assert.Equal(4, retryStart.Living.Charge);
        var retryInputs = Channel.CreateUnbounded<SoloInputItem>();
        var retryStream = hub.InvokeAsync<GhostLapReply<InputStreamSummary>>("StreamChallengeInputs", retryInputs.Reader, ct);
        await retryInputs.Writer.WriteAsync(new SoloInputItem(100000, -1000, 0, 0, true), ct);
        for (var tries=0; ; tries++)
        {
            lock (rejections) { if (rejections.Any(r => r.Seq == 100000 && r.Class == "burst_rejected" && r.Code == "LateTick")) break; }
            Assert.True(tries < 100); await Task.Delay(10, ct);
        }
        var retrySeq = 100000;
        ChallengeFrameView retryFrame;
        do
        {
            retryFrame = await frames.Reader.ReadAsync(ct);
            Assert.InRange(retryFrame.Tick, armed.Tick+1, armed.Tick+30);
            if ((retryFrame.Living.Events ?? []).Any(e => e.Kind == "BurstManual")) break;
            await retryInputs.Writer.WriteAsync(new SoloInputItem(++retrySeq, retryFrame.Tick+1, 0, 0, true), ct);
        } while (true);
        Assert.Single(retryFrame.Living.Events!, e => e.Kind == "BurstManual");
        Assert.Equal(0, retryFrame.Living.Charge);
        retryInputs.Writer.Complete(); await retryStream.WaitAsync(ct);
        Assert.True((await hub.InvokeAsync<GhostLapReply<bool>>("CancelChallenge", ct)).Value);

        var challenge = (await hub.InvokeAsync<GhostLapReply<ChallengePrepared>>("PrepareChallenge", new PrepareChallengeRequest(sourceId), ct)).Value!;
        Assert.Equal(("Challenge", 0L, 1L), (challenge.Kind, challenge.ForkTick, challenge.FirstInputTick));
        var challengeStarted = (await hub.InvokeAsync<GhostLapReply<ChallengeStarted>>("StartChallenge", new StartChallengeRequest("route-challenge"), ct)).Value!;
        Assert.Equal(("Challenge", 0L, 1L, "challenge.route-challenge", 0L, 0L), (challengeStarted.Kind, challengeStarted.ForkTick, challengeStarted.FirstInputTick,
            challengeStarted.OperationKey, challengeStarted.Living.Tick, challengeStarted.Ghost.Tick));
        AssertCandles(plan, 0, challengeStarted.Living.ActiveCandleIds!, challengeStarted.Living.VisibleCandles);
        Assert.NotEmpty(challengeStarted.Living.VisibleCandles!);
        Assert.True((await hub.InvokeAsync<GhostLapReply<bool>>("CancelChallenge", ct)).Value);
        await summary.WaitAsync(ct);
    }
}
