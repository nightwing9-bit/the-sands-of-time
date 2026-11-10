using System.Globalization;
using System.Net;
using System.Text.Json;
using Integration.Tests.Support;
using Marrow.Infrastructure.Contracts;
using Marrow.Infrastructure.Timeline;
using Marrow.Tests.TestDomains;
using Marrow.Timeline;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Sands.E2E.Backend;
using Xunit;
namespace Integration.Tests.Timeline;
/// <summary>
/// Spec 04 exit gate, backend half: the test-only Timeline host executes the real engine over real PostgreSQL. These tests call it
/// over HTTP as a browser would (guest cookie, Origin, CSRF) and compare every answer with an independent source: a hand-computed
/// fold, the hashes stored in the <c>steps</c> table, or a cold engine on a fresh connection pool. The same classes serve the
/// Playwright suite in <c>tests/E2E/real-backend.spec.ts</c>.
/// </summary>
[Collection(E2eTimelineHostCollection.Name)]
[Trait("Category", "Integration")]
public sealed class E2eTimelineHostTests(E2eTimelineHostFixture fixture)
{
    private static readonly DateTime BaseTime = new(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc);
    private static async Task<TestBrowser> GuestAsync(E2eTimelineHostFixture fixture)
    {
        var browser = fixture.NewBrowser();
        var started = await browser.PostAsync("/auth/guest/start");
        Assert.Equal(HttpStatusCode.OK, started.Status);
        return browser;
    }
    private static KeyValuePair<string, string> Token => new(E2eTimelineEndpoints.FixtureTokenHeader, E2eTimelineHostFixture.FixtureToken);
    private static Task<ApiResponse> PostAsync(TestBrowser browser, string path, object body, params KeyValuePair<string, string>[] headers) =>
        browser.SendAsync(HttpMethod.Post, path, body, csrfCookie: Flows.GuestCsrf, headers: headers);
    private static async Task<string> ProvisionAsync(TestBrowser browser, object request)
    {
        var response = await PostAsync(browser, "/__e2e/provision", request, Token);
        Assert.Equal(HttpStatusCode.OK, response.Status);
        return response.Str("timelineId");
    }
    private static Task<ApiResponse> ForkAsync(TestBrowser browser, string id, string position, string key) =>
        PostAsync(browser, $"/timeline/{id}/fork", new { position, operationKey = key, options = (object?)null }, new KeyValuePair<string, string>("Idempotency-Key", key));
    private async Task<JsonElement> FactsAsync(TestBrowser browser, string id, string? position = null)
    {
        var response = await browser.GetAsync($"/__e2e/facts/{id}{(position is null ? "" : "?position=" + position)}", headers: [Token]);
        Assert.Equal(HttpStatusCode.OK, response.Status);
        return response.Json.Clone();
    }
    [Fact]
    public async Task Revenant_shape_restore_returns_the_verified_event_state_that_matches_PostgreSQL_and_an_independent_fold()
    {
        var browser = await GuestAsync(fixture);
        var id = await ProvisionAsync(browser, new { domain = "revenant" });
        var described = await browser.GetAsync($"/timeline/{id}");
        Assert.Equal(HttpStatusCode.OK, described.Status);
        Assert.Equal("0", described.Json.GetProperty("bounds").GetProperty("start").GetString());
        Assert.Equal("24", described.Json.GetProperty("bounds").GetProperty("end").GetString());
        Assert.Equal("Verified", described.Str("historyStatus"));
        Assert.Equal(id, described.Str("sourceId"));
        Assert.Equal(JsonValueKind.Null, described.Json.GetProperty("branchId").ValueKind);
        // Hand fold: step p adds (100 + p) first when p % 4 == 0, then p; value = value * 2 + amount; closing event time is base + p(p+7) minutes.
        long value = 0, events = 0;
        for (var p = 1L; p <= 24; p++)
        {
            if (p % 4 == 0)
            {
                value = value * 2 + 100 + p;
                events++;
            }
            value = value * 2 + p;
            events++;
            if (p is not (0 or 1 or 3 or 4 or 5 or 8 or 24))
            {
                continue;
            }
            var restored = await browser.GetAsync($"/timeline/{id}/restore?position={p}");
            Assert.Equal(HttpStatusCode.OK, restored.Status);
            var state = restored.Json.GetProperty("state");
            Assert.Equal(p.ToString(CultureInfo.InvariantCulture), restored.Json.GetProperty("position").GetString());
            Assert.Equal(BaseTime.AddMinutes(p * (p + 7)).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), state.GetProperty("businessTime").GetString());
            Assert.Equal(events, state.GetProperty("eventsApplied").GetInt64());
            Assert.Equal(value, state.GetProperty("value").GetInt64());
            var facts = await FactsAsync(browser, id, p.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(facts.GetProperty("stateHashAt").GetString(), state.GetProperty("stateHash").GetString());
        }
        var initial = await browser.GetAsync($"/timeline/{id}/restore?position=0");
        Assert.Equal("2026-03-02T09:00:00Z", initial.Json.GetProperty("state").GetProperty("businessTime").GetString());
        Assert.Equal("StoredCheckpoint", initial.Str("hashOrigin"));
    }
    [Fact]
    public async Task Ghost_lap_shape_restore_returns_the_verified_tick_state_and_never_a_number_for_the_position()
    {
        var browser = await GuestAsync(fixture);
        var id = await ProvisionAsync(browser, new { domain = "ghost-lap" });
        var restored = await browser.GetAsync($"/timeline/{id}/restore?position=7");
        Assert.Equal(HttpStatusCode.OK, restored.Status);
        Assert.Equal(JsonValueKind.String, restored.Json.GetProperty("position").ValueKind);
        var state = restored.Json.GetProperty("state");
        Assert.Equal(7, state.GetProperty("tick").GetInt64());
        // Hand fold (cooldown 3, timer 90): bursts on odd ticks only when cooldown is 0; cooldown drains one per tick.
        int timer = 90, cooldown = 0, bursts = 0;
        for (var tick = 1; tick <= 7; tick++)
        {
            timer = Math.Max(0, timer - 1);
            cooldown = Math.Max(0, cooldown - 1);
            if (tick % 2 == 1 && cooldown == 0)
            {
                bursts++;
                cooldown = 3;
            }
        }
        Assert.Equal(timer, state.GetProperty("timer").GetInt32());
        Assert.Equal(cooldown, state.GetProperty("cooldown").GetInt32());
        Assert.Equal(bursts, state.GetProperty("bursts").GetInt32());
        var facts = await FactsAsync(browser, id, "7");
        Assert.Equal(facts.GetProperty("stateHashAt").GetString(), state.GetProperty("stateHash").GetString());
    }
    [Fact]
    public async Task Positions_are_parsed_as_exact_canonical_decimal_int64_and_everything_else_is_a_typed_InvalidPosition()
    {
        var browser = await GuestAsync(fixture);
        var id = await ProvisionAsync(browser, new { domain = "revenant" });
        // 2^53 + 1 is not representable as a double; the server read it exactly and found it beyond the history.
        var big = await browser.GetAsync($"/timeline/{id}/restore?position=9007199254740993");
        Assert.Equal(HttpStatusCode.BadRequest, big.Status);
        Assert.Equal("InvalidPosition", big.Str("timelineError"));
        Assert.Equal("9007199254740993", big.Str("requestedPosition"));
        var max = await browser.GetAsync($"/timeline/{id}/restore?position=9223372036854775807");
        Assert.Equal("9223372036854775807", max.Str("requestedPosition"));
        var beyondLastByOne = await browser.GetAsync($"/timeline/{id}/restore?position=25");
        Assert.Equal("InvalidPosition", beyondLastByOne.Str("timelineError"));
        Assert.Equal("25", beyondLastByOne.Str("requestedPosition"));
        foreach (var bad in new[] { "9223372036854775808", "007", "-1", "1.5", "1e3", "+1", "%201", "", "abc" })
        {
            var response = await browser.GetAsync($"/timeline/{id}/restore?position={bad}");
            Assert.Equal(HttpStatusCode.BadRequest, response.Status);
            Assert.Equal("InvalidPosition", response.Str("timelineError"));
            Assert.False(response.Json.TryGetProperty("requestedPosition", out _), $"'{bad}' must not be echoed as a parsed position");
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.GetAsync($"/timeline/{id}/restore")).Status);
    }
    [Fact]
    public async Task A_stranger_an_unknown_id_and_an_anonymous_caller_are_denied_without_any_state_or_branch()
    {
        var owner = await GuestAsync(fixture);
        var id = await ProvisionAsync(owner, new { domain = "ghost-lap" });
        var stranger = await GuestAsync(fixture);
        var denied = await stranger.GetAsync($"/timeline/{id}/restore?position=3");
        var unknown = await stranger.GetAsync($"/timeline/{Guid.NewGuid()}/restore?position=3");
        Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
        Assert.Equal("Forbidden", denied.Str("timelineError"));
        Assert.False(denied.Json.TryGetProperty("state", out _));
        // No existence oracle: a real foreign history and a made-up id answer identically (apart from the trace id).
        Assert.Equal(unknown.Status, denied.Status);
        Assert.Equal(unknown.Str("code"), denied.Str("code"));
        Assert.Equal(unknown.Str("timelineError"), denied.Str("timelineError"));
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync($"/timeline/{id}")).Status);
        var strangerFork = await ForkAsync(stranger, id, "3", "stranger-key");
        Assert.Equal(HttpStatusCode.Forbidden, strangerFork.Status);
        Assert.Equal(0, (await FactsAsync(owner, id)).GetProperty("children").GetArrayLength());
        var anonymous = fixture.NewBrowser();
        var noSession = await anonymous.GetAsync($"/timeline/{id}/restore?position=3");
        Assert.Equal(HttpStatusCode.Unauthorized, noSession.Status);
        Assert.Equal("session_inactive", noSession.Code);
        // The owner still reads their own history.
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/timeline/{id}/restore?position=3")).Status);
        // Provisioning is fixture-scoped: no token, no fixture.
        var noToken = await owner.SendAsync(HttpMethod.Post, "/__e2e/provision", new { domain = "revenant" }, csrfCookie: Flows.GuestCsrf);
        Assert.Equal(HttpStatusCode.Forbidden, noToken.Status);
        Assert.Equal("fixture_token_required", noToken.Code);
    }
    [Fact]
    public async Task Fork_is_persisted_as_an_independent_child_is_idempotent_by_key_and_never_changes_the_source()
    {
        var browser = await GuestAsync(fixture);
        var id = await ProvisionAsync(browser, new { domain = "ghost-lap" });
        var before = await FactsAsync(browser, id);
        var first = await ForkAsync(browser, id, "7", "fork-key-1");
        var repeated = await ForkAsync(browser, id, "7", "fork-key-1");
        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal(HttpStatusCode.OK, repeated.Status);
        var child = first.Str("branchId");
        Assert.Equal(child, repeated.Str("branchId"));
        Assert.NotEqual(id, child);
        var afterRepeat = await FactsAsync(browser, id);
        var children = afterRepeat.GetProperty("children");
        Assert.Equal(1, children.GetArrayLength());
        Assert.Equal(child, children[0].GetProperty("id").GetString());
        Assert.Equal("fork-key-1", children[0].GetProperty("operationKey").GetString());
        Assert.Equal("7", children[0].GetProperty("forkPosition").GetString());
        Assert.Equal("7", children[0].GetProperty("lastPosition").GetString());
        // A new key at the same position is a different intent and a second, distinct child.
        var other = await ForkAsync(browser, id, "7", "fork-key-2");
        Assert.NotEqual(child, other.Str("branchId"));
        Assert.Equal(2, (await FactsAsync(browser, id)).GetProperty("children").GetArrayLength());
        // The source row is untouched by either fork.
        Assert.Equal(before.GetProperty("history").GetRawText(), afterRepeat.GetProperty("history").GetRawText());
        // The child restores from the database: prefix equals the source prefix and the hashes stored for the source.
        var describedChild = await browser.GetAsync($"/timeline/{child}");
        Assert.Equal(HttpStatusCode.OK, describedChild.Status);
        Assert.Equal(child, describedChild.Str("branchId"));
        Assert.Equal(id, describedChild.Str("sourceId"));
        Assert.Equal("7", describedChild.Json.GetProperty("bounds").GetProperty("end").GetString());
        foreach (var position in new[] { 0, 3, 7 })
        {
            var sourceView = await browser.GetAsync($"/timeline/{id}/restore?position={position}");
            var childView = await browser.GetAsync($"/timeline/{child}/restore?position={position}");
            Assert.Equal(sourceView.Json.GetProperty("state").GetProperty("stateHash").GetString(), childView.Json.GetProperty("state").GetProperty("stateHash").GetString());
            var stored = await FactsAsync(browser, child, position.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(stored.GetProperty("stateHashAt").GetString(), childView.Json.GetProperty("state").GetProperty("stateHash").GetString());
        }
        var past = await browser.GetAsync($"/timeline/{child}/restore?position=8");
        Assert.Equal("InvalidPosition", past.Str("timelineError"));
        // The child is owned by the same verified subject: a stranger cannot read it.
        var stranger = await GuestAsync(fixture);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync($"/timeline/{child}")).Status);
        // A Recording child is not a Verified fork source in Timeline v1: typed Conflict, and no grandchild appears.
        var grandchild = await ForkAsync(browser, child, "3", "fork-from-child");
        Assert.Equal(HttpStatusCode.Conflict, grandchild.Status);
        Assert.Equal("Conflict", grandchild.Str("timelineError"));
        Assert.False(grandchild.Json.TryGetProperty("branchId", out _));
        Assert.Equal(0, (await FactsAsync(browser, child)).GetProperty("children").GetArrayLength());
    }
    [Fact]
    public async Task A_forked_child_continues_under_its_own_context_and_restores_on_a_cold_engine_while_the_source_is_unchanged()
    {
        var browser = await GuestAsync(fixture);
        var id = await ProvisionAsync(browser, new { domain = "ghost-lap" });
        var child = (await ForkAsync(browser, id, "7", "continue-key")).Str("branchId");
        var sourceBefore = (await FactsAsync(browser, id)).GetProperty("history").GetRawText();
        var engine = fixture.Factory.Services.GetRequiredService<ITimelineEngine<TickWorld, TickInput, TickContext>>();
        var ct = CancellationToken.None;
        var childId = new TimelineId(Guid.Parse(child));
        // Continue the child with the same inputs the source has at 8..20; only the pinned context (cooldown 5, not 3) differs.
        var reference = (await engine.RestoreAsync(new TimelineId(Guid.Parse(id)), 7, ct)).Value.State;
        for (var p = 8L; p <= 20; p++)
        {
            var inputs = new List<OrderedInput<TickInput>>();
            if (p % 2 == 1)
            {
                inputs.Add(new OrderedInput<TickInput>($"c{p:D4}-b", 0, new TickInput(TickAction.Burst, "")));
            }
            if (p % 3 == 0)
            {
                inputs.Add(new OrderedInput<TickInput>($"c{p:D4}-c", 1, new TickInput(TickAction.Collect, $"item-{p % 4}")));
            }
            var step = new StepRecord<TickInput>(p, inputs);
            Assert.True((await engine.AppendAsync(childId, p - 1, step, $"cont-{p}", ct)).IsSuccess);
            reference = new TickReducer().Apply(reference, p, inputs, new TickContext(5));
        }
        // Cold engine: a brand-new connection pool and engine instance, nothing shared with the host under test.
        await using var coldSource = new NpgsqlDataSourceBuilder(fixture.Database.ApiConnectionString).Build();
        await using var cold = new ServiceCollection().AddSingleton(coldSource)
            .AddMarrowTimeline([new TimelineComponents<TickWorld, TickInput, TickContext>(
                FixtureDomains.GhostLapKey, new TickReducer(), new TickWorldCodec(), new TickInputCodec(), new TickContextCodec())], 4)
            .BuildServiceProvider();
        var coldEngine = cold.GetRequiredService<ITimelineEngine<TickWorld, TickInput, TickContext>>();
        var restoredChild = await coldEngine.RestoreAsync(childId, 20, ct);
        Assert.True(restoredChild.IsSuccess, restoredChild.ToString());
        var codec = new TickWorldCodec();
        Assert.Equal(codec.EncodeCanonical(reference), codec.EncodeCanonical(restoredChild.Value.State));
        var restoredSource = await coldEngine.RestoreAsync(new TimelineId(Guid.Parse(id)), 20, ct);
        Assert.NotEqual(restoredSource.Value.StateHash, restoredChild.Value.StateHash);
        // The shared prefix is identical on both sides of the fork and the source row did not move.
        Assert.Equal(
            (await coldEngine.RestoreAsync(new TimelineId(Guid.Parse(id)), 7, ct)).Value.StateHash,
            (await coldEngine.RestoreAsync(childId, 7, ct)).Value.StateHash);
        Assert.Equal(sourceBefore, (await FactsAsync(browser, id)).GetProperty("history").GetRawText());
        // Over HTTP the extended child reports its new bound and still refuses the position the source never gets to.
        Assert.Equal("20", (await browser.GetAsync($"/timeline/{child}")).Json.GetProperty("bounds").GetProperty("end").GetString());
    }
    [Fact]
    public async Task Fork_requests_that_are_malformed_or_not_idempotency_keyed_are_rejected_before_the_engine()
    {
        var browser = await GuestAsync(fixture);
        var id = await ProvisionAsync(browser, new { domain = "revenant" });
        var mismatch = await PostAsync(browser, $"/timeline/{id}/fork", new { position = "3", operationKey = "a", options = (object?)null },
            new KeyValuePair<string, string>("Idempotency-Key", "b"));
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.Status);
        Assert.Equal("invalid_request", mismatch.Code);
        var noHeader = await PostAsync(browser, $"/timeline/{id}/fork", new { position = "3", operationKey = "a", options = (object?)null });
        Assert.Equal(HttpStatusCode.BadRequest, noHeader.Status);
        var badOptions = await PostAsync(browser, $"/timeline/{id}/fork", new { position = "3", operationKey = "a", options = new[] { 1 } },
            new KeyValuePair<string, string>("Idempotency-Key", "a"));
        Assert.Equal(HttpStatusCode.BadRequest, badOptions.Status);
        var numericPosition = await browser.SendAsync(HttpMethod.Post, $"/timeline/{id}/fork", new { position = 3, operationKey = "a" },
            csrfCookie: Flows.GuestCsrf, headers: [new KeyValuePair<string, string>("Idempotency-Key", "a")]);
        Assert.Equal(HttpStatusCode.BadRequest, numericPosition.Status);
        var beyond = await ForkAsync(browser, id, "25", "beyond");
        Assert.Equal(HttpStatusCode.BadRequest, beyond.Status);
        Assert.Equal("InvalidPosition", beyond.Str("timelineError"));
        // Without the CSRF token the real identity middleware refuses the mutation before any Timeline code runs.
        var noCsrf = await browser.SendAsync(HttpMethod.Post, $"/timeline/{id}/fork", new { position = "3", operationKey = "a" },
            headers: [new KeyValuePair<string, string>("Idempotency-Key", "a")]);
        Assert.Equal(HttpStatusCode.Forbidden, noCsrf.Status);
        Assert.Equal("csrf_rejected", noCsrf.Code);
        Assert.Equal(0, (await FactsAsync(browser, id)).GetProperty("children").GetArrayLength());
    }
    [Fact]
    public async Task Incompatible_and_corrupt_histories_are_typed_failures_and_never_a_state()
    {
        var browser = await GuestAsync(fixture);
        var incompatible = await ProvisionAsync(browser, new { domain = "revenant", scenario = "incompatible" });
        var described = await browser.GetAsync($"/timeline/{incompatible}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, described.Status);
        Assert.Equal("IncompatibleVersion", described.Str("timelineError"));
        var restored = await browser.GetAsync($"/timeline/{incompatible}/restore?position=1");
        Assert.Equal("IncompatibleVersion", restored.Str("timelineError"));
        Assert.False(restored.Json.TryGetProperty("state", out _));
        var fork = await ForkAsync(browser, incompatible, "1", "incompatible-fork");
        Assert.Equal("IncompatibleVersion", fork.Str("timelineError"));
        Assert.Equal(0, (await FactsAsync(browser, incompatible)).GetProperty("children").GetArrayLength());
        var corrupt = await ProvisionAsync(browser, new { domain = "ghost-lap", scenario = "corrupt-checkpoint" });
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync($"/timeline/{corrupt}")).Status);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync($"/timeline/{corrupt}/restore?position=7")).Status);
        foreach (var position in new[] { 8, 9, 11 })
        {
            var bad = await browser.GetAsync($"/timeline/{corrupt}/restore?position={position}");
            Assert.Equal(HttpStatusCode.InternalServerError, bad.Status);
            Assert.Equal("CorruptHistory", bad.Str("timelineError"));
            Assert.False(bad.Json.TryGetProperty("state", out _));
        }
        var later = await browser.GetAsync($"/timeline/{corrupt}/restore?position=12");
        Assert.Equal(HttpStatusCode.OK, later.Status);
        Assert.Equal("StoredCheckpoint", later.Str("hashOrigin"));
    }
    [Fact]
    public async Task A_provisioned_slow_restore_is_observed_as_aborted_when_the_caller_gives_up()
    {
        var browser = await GuestAsync(fixture);
        var id = await ProvisionAsync(browser, new { domain = "revenant", slowRestoreMs = new Dictionary<string, int> { ["3"] = 2500 } });
        using var impatient = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var client = fixture.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/timeline/{id}/restore?position=3");
        request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", browser.Cookies.Select(c => $"{c.Key}={c.Value}")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(request, impatient.Token));
        JsonElement entries = default;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var log = await browser.GetAsync($"/__e2e/log?timeline={id}", headers: [Token]);
            entries = log.Json.Clone();
            if (entries.EnumerateArray().Any(e => e.GetProperty("outcome").GetString() == "aborted"))
            {
                break;
            }
            await Task.Delay(100);
        }
        var aborted = entries.EnumerateArray().Single(e => e.GetProperty("outcome").GetString() == "aborted");
        Assert.Equal("?position=3", aborted.GetProperty("query").GetString());
        // A fast position is unaffected by the slow one.
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync($"/timeline/{id}/restore?position=4")).Status);
    }
    [Fact]
    public void Every_Timeline_error_code_has_one_distinct_typed_http_mapping_and_the_handshake_lists_the_same_set()
    {
        var codes = Enum.GetValues<TimelineErrorCode>();
        Assert.Equal(codes.Length, codes.Select(TimelineHttpErrors.Status).Distinct().Count());
        Assert.Equal(codes.Length, codes.Select(TimelineHttpErrors.Code).Distinct().Count());
        foreach (var code in codes)
        {
            var problem = TimelineHttpErrors.Problem(new DefaultHttpContext(), code, "5");
            Assert.Equal(code, problem.TimelineError);
            Assert.Equal(TimelineHttpErrors.Status(code), problem.Status);
            Assert.InRange(problem.Status, 400, 599);
        }
        Assert.Equal(ContractMetadata.Current.TimelineErrorCodes.Order(), codes.Order());
        Assert.Throws<System.Diagnostics.UnreachableException>(() => TimelineHttpErrors.Status((TimelineErrorCode)999));
    }
}
