using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Integration.Tests.Support;
using Marrow.Infrastructure.Timeline;
using Marrow.Kernel.Canonical;
using Revenant.Api;
using Revenant.Cases;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Intelligence;
using Revenant.Payments;
using Revenant.Scenario;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Revenant.Counterfactual;
using Revenant.Timeline;
using Xunit;

namespace Integration.Tests.RevenantLedger;

[Collection(IdentityCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RevenantR8ApiTests(IdentityDatabase db)
{
    [Fact]
    public async Task R8_second_approval_refreshes_only_after_distinct_supervisor_commits()
    {
        const string tenant = "r8-four-eyes";
        await using var setup = db.Owner.CreateCommand("""
            CREATE ROLE r8_review_ledger LOGIN PASSWORD 'fixtureReviewLedgerOnly0001' IN ROLE revenant_ledger_writer;
            CREATE ROLE r8_review_timeline LOGIN PASSWORD 'fixtureReviewTimeline0001' IN ROLE marrow_timeline_runtime;
            CREATE ROLE r8_review_replay LOGIN PASSWORD 'fixtureReviewReplayOnly0001'
                IN ROLE marrow_timeline_revenant_replay, revenant_replay_reader, revenant_counterfactual_worker;
            INSERT INTO revenant.counterfactual_worker_tenant VALUES ('r8_review_replay', 'r8-four-eyes');
            """);
        await setup.ExecuteNonQueryAsync();
        var ledger = Connection("r8_review_ledger", "fixtureReviewLedgerOnly0001");
        var timeline = Connection("r8_review_timeline", "fixtureReviewTimeline0001");
        await using var api = new IdentityApiFactory(db, extraSettings: new()
        { ["ConnectionStrings:RevenantLedger"] = ledger, ["ConnectionStrings:RevenantTimeline"] = timeline });
        var first = await Staff("supervisor");
        var second = await Staff("supervisor");
        var wrongRole = await Staff("operator");
        const long origin = 1_761_000_000_000;
        ScenarioRunId id = new(Guid.NewGuid());
        var run = BehavioralEpisodeScenario.Create(id, 42, origin, 600_000, [2, 2, 2, 2, 2, 2]);
        var ctx = new ObservedFinanceContext(run.Manifest.ScenarioVersion, run.Manifest.ContentHash, origin,
            PolicyCatalog.RefOf(PolicyCatalog.V7), ReviewPolicy.Version, ObservedRiskMode.ModelRequired,
            BehavioralRiskScorerV2.FeatureSchema, BehavioralRiskScorerV2.PublishedPin, [], []);
        var store = api.Services.GetRequiredService<V4SimulationStore>();
        var branch = api.Services.GetRequiredService<V4BranchExecution>();
        await branch.OpenSourceAsync(tenant, first.Subject, run, ctx, default);
        var liveLedger = api.Services.GetRequiredService<PaymentLedgerStore>();
        Assert.True(await liveLedger.GrantRunStaffAsync(id, first.Subject, "supervisor", default));
        Assert.True(await liveLedger.GrantRunStaffAsync(id, second.Subject, "supervisor", default));
        Assert.True(await liveLedger.GrantRunStaffAsync(id, wrongRole.Subject, "operator", default));
        var from = run.Manifest.Accounts.First(x => x.OpeningBalance.MinorUnits >= ReviewPolicy.HighValueThresholdMinor);
        var to = run.Manifest.Accounts.First(x => x.BankId != from.BankId);
        var payment = new PaymentIntent(id, "review-two", 1, origin, from.BankId, from.AccountId, to.BankId, to.AccountId,
            Money.Usd(ReviewPolicy.HighValueThresholdMinor), SyntheticTruth.Legitimate, new(false, true));
        await Dispatch(new("review-intake", V4CommandClass.Exogenous, nameof(ObservedFactKind.PaymentIntent),
            ScenarioSchemas.InputSchemaVersion, [.. PaymentIntentCodec.Instance.EncodeCanonical(payment)], origin, origin));
        await Dispatch(new("review-screen", V4CommandClass.DerivedOpportunity, nameof(ObservedFactKind.Screening),
            "v4-payment-opportunity/1", [.. new CanonicalWriter().WriteInt32(1).WriteString(payment.IntentId).ToArray()], origin, origin));
        Assert.Equal(V4StepKind.Sealed, (await store.SealAsync(tenant, id, origin + 300_000, default)).Kind);
        await branch.SynchronizeAsync(tenant, id, default);

        using var client = api.CreateClient();
        var path = $"/revenant/r8/runs/{id.Value}";
        var before = await Send(HttpMethod.Get, path, first.Token, HttpStatusCode.OK);
        var held = Assert.Single(before.GetProperty("cases").EnumerateArray());
        Assert.Equal(2, held.GetProperty("requiredApprovals").GetInt32());
        var casePath = path + "/cases/" + payment.IntentId;
        var approved = await Review(first.Token, "first", 1, HttpStatusCode.OK);
        Assert.Equal("AwaitingSecondApproval", approved.GetProperty("status").GetString());
        Assert.Equal(1, approved.GetProperty("approvalCount").GetInt32());
        await Review(first.Token, "same", 2, HttpStatusCode.Conflict);
        await Review(wrongRole.Token, "wrong-role", 2, HttpStatusCode.Conflict);
        await Review(second.Token, "stale", 1, HttpStatusCode.Conflict);
        var final = await Review(second.Token, "second", 2, HttpStatusCode.OK);
        Assert.Equal("Released", final.GetProperty("status").GetString());
        Assert.Equal(2, final.GetProperty("approvalCount").GetInt32());
        Assert.Equal("Settled", (await liveLedger.GetPaymentAsync(tenant, id, payment.IntentId, default))!.Status.ToString());
        var after = await Send(HttpMethod.Get, path, first.Token, HttpStatusCode.OK);
        Assert.Equal("Settled", Assert.Single(after.GetProperty("payments").EnumerateArray()).GetProperty("status").GetString());
        Assert.Equal((await liveLedger.GetCaseAsync(tenant, id, payment.IntentId, default))!.Version,
            Assert.Single(after.GetProperty("cases").EnumerateArray()).GetProperty("version").GetInt64());
        var refreshedBank = after.GetProperty("balances").EnumerateArray().Single(b => b.GetProperty("bankId").GetString() == from.BankId);
        Assert.Equal("0", refreshedBank.GetProperty("reservedMinor").GetString());

        var horizon = origin + 600_000;
        Assert.Equal(V4StepKind.Sealed, (await store.SealAsync(tenant, id, horizon, default)).Kind);
        await branch.CompleteAsync(tenant, id, default);
        var fork = Assert.Single(before.GetProperty("markers").EnumerateArray(), m =>
            m.GetProperty("kind").GetString() == "ObservationSeal").GetProperty("position").GetString()!;
        var services = new ServiceCollection();
        var replayConnection = new NpgsqlConnectionStringBuilder(Connection("r8_review_replay", "fixtureReviewReplayOnly0001"))
            { Options = $"-c revenant.tenant_id={tenant}" }.ConnectionString;
        services.AddSingleton(_ => NpgsqlDataSource.Create(replayConnection));
        services.AddMarrowTimeline(ObservedFinanceTimelineVersions.All, ObservedFinanceTimelineVersions.CheckpointInterval);
        services.AddSingleton<V4ReplayWorker>();
        await using var replay = services.BuildServiceProvider();
        var policy = await Replay("policy-only", null);
        Assert.Equal("PolicyOnly", policy.GetProperty("mode").GetString());
        var recorded = policy.GetProperty("actions").EnumerateArray().ToArray();
        Assert.Equal(new[] { first.Subject, second.Subject }, recorded.Select(x => x.GetProperty("actor").GetString()));
        Assert.All(recorded, x =>
        {
            Assert.Equal("Applied", x.GetProperty("status").GetString());
            Assert.NotNull(x.GetProperty("fact").GetString());
        });
        var sandbox = await Replay("with-intervention",
            [new(payment.IntentId, "Reject", 3, origin + 300_001)]);
        Assert.Equal("PolicyAndSandboxIntervention", sandbox.GetProperty("mode").GetString());
        var actions = sandbox.GetProperty("actions").EnumerateArray().ToArray();
        Assert.Equal(3, actions.Length);
        Assert.Equal("InapplicableUnderBranch", actions[2].GetProperty("status").GetString());
        Assert.Equal("Reject", actions[2].GetProperty("action").GetString());
        Assert.Equal(first.Subject, actions[2].GetProperty("actor").GetString());
        Assert.Equal(JsonValueKind.Null, actions[2].GetProperty("fact").ValueKind);

        async Task<JsonElement> Replay(string key, R8Intervention[]? interventions)
        {
            var job = await Send(HttpMethod.Post, path + "/counterfactual", first.Token, HttpStatusCode.OK, key,
                new R8Replay(fork, horizon, PolicyCatalog.V8Id, interventions));
            var jobId = job.GetProperty("jobId").GetGuid();
            var processed = await replay.GetRequiredService<V4ReplayWorker>().ProcessAsync(tenant, jobId, default);
            var result = await Send(HttpMethod.Get, path + $"/counterfactual/{jobId}", first.Token, HttpStatusCode.OK);
            Assert.True(processed && result.GetProperty("status").GetString() == "Verified", result.GetRawText());
            return result.GetProperty("verified");
        }

        string Connection(string name, string password) => new NpgsqlConnectionStringBuilder(db.AppConnectionString)
            { Username = name, Password = password }.ConnectionString;

        async Task<(string Subject, string Token)> Staff(string role)
        {
            var staff = await api.CreateStaffAsync(role, tenant);
            var (browser, access, _) = await Flows.LoginAsync(api, staff);
            await Flows.EnrollAsync(browser, access, staff);
            return ($"staff:{staff.Id:D}", access);
        }

        async Task Dispatch(V4CommandRequest request)
        {
            await store.AdmitAsync(tenant, id, request, default);
            Assert.Equal(V4CommandStatus.Completed, (await store.AdvanceOneStepAsync(tenant, id, default)).Command?.Status);
        }

        Task<JsonElement> Review(string token, string key, long version, HttpStatusCode expected) =>
            Send(HttpMethod.Post, casePath, token, expected, key, new R8Review("Approve", version));

        async Task<JsonElement> Send(HttpMethod method, string uri, string token, HttpStatusCode expected,
            string? key = null, object? body = null)
        {
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.Authorization = new("Bearer", token);
            if (key is not null) request.Headers.Add("Idempotency-Key", key);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await client.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == expected, $"{method} {uri}: {response.StatusCode} {json}");
            return JsonDocument.Parse(json).RootElement.Clone();
        }
    }

    [Fact]
    public async Task Fresh_scenarios_reviews_and_verified_replay_use_real_authenticated_tenant()
    {
        const string tenant = "r8-fixture";
        var ledger = await Login("r8_api_ledger", "revenant_ledger_writer");
        var timeline = await Login("r8_api_timeline", "marrow_timeline_runtime");
        var replay = await Login("r8_worker", "marrow_timeline_revenant_replay, revenant_replay_reader, revenant_counterfactual_worker");
        await using (var bind = db.Owner.CreateCommand("INSERT INTO revenant.counterfactual_worker_tenant VALUES ('r8_worker', 'r8-fixture')"))
            await bind.ExecuteNonQueryAsync();
        await using var api = new IdentityApiFactory(db, extraSettings: new()
        { ["ConnectionStrings:RevenantLedger"] = ledger, ["ConnectionStrings:RevenantTimeline"] = timeline });
        var staff = await api.CreateStaffAsync("operator", tenant);
        var (browser, access, _) = await Flows.LoginAsync(api, staff);
        await Flows.EnrollAsync(browser, access, staff);
        var other = await api.CreateStaffAsync("operator", "other-r8");
        var (_, otherToken, _) = await Flows.LoginAsync(api, other);
        using var client = api.CreateClient();

        using var noAccess = await client.GetAsync("/revenant/r8/runs");
        Assert.Equal(HttpStatusCode.Unauthorized, noAccess.StatusCode);
        var (_, first) = await Send(HttpMethod.Post, "/revenant/r8/runs", new { seed = "42" }, "start");
        var runId = first.GetProperty("runId").GetGuid();
        var path = $"/revenant/r8/runs/{runId}";
        var (_, retry) = await Send(HttpMethod.Post, "/revenant/r8/runs", new { seed = "42" }, "start");
        Assert.Equal(runId, retry.GetProperty("runId").GetGuid());
        var (conflict, _) = await Send(HttpMethod.Post, "/revenant/r8/runs", new { seed = "43" }, "start", expect: HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.Conflict, conflict);
        _ = await Send(HttpMethod.Get, path, token: otherToken, expect: HttpStatusCode.Forbidden);
        var (_, snap) = await Send(HttpMethod.Get, path);
        Assert.Equal(6, snap.GetProperty("balances").GetArrayLength());
        Assert.Equal(4, snap.GetProperty("payments").GetArrayLength());
        var held = Assert.Single(snap.GetProperty("cases").EnumerateArray());
        Assert.Equal("Open", held.GetProperty("status").GetString());
        Assert.True(held.GetProperty("risk").GetProperty("scorePpm").GetInt32() >= 0);
        var casePath = path + "/cases/" + held.GetProperty("intentId").GetString();
        var (_, reviewed) = await Send(HttpMethod.Post, casePath, new { action = "Approve", expectedVersion = 1 }, "approve");
        Assert.Equal("Released", reviewed.GetProperty("status").GetString());
        _ = await Send(HttpMethod.Post, casePath, new { action = "Reject", expectedVersion = 1 }, "stale", expect: HttpStatusCode.Conflict);
        var (_, closed) = await Send(HttpMethod.Post, path + "/close", key: "close");
        Assert.Equal("Verified", closed.GetProperty("status").GetString());
        var seal = closed.GetProperty("markers").EnumerateArray().Single(x => x.GetProperty("position").GetString() == "288");
        var at = seal.GetProperty("atMs").GetInt64();
        var horizon = closed.GetProperty("boundaryMs").GetInt64();
        var (_, job) = await Send(HttpMethod.Post, path + "/counterfactual", new
        {
            forkPosition = "288", horizonMs = horizon, policy = "policy-v8",
            interventions = new[] { new { intentId = "P-001", action = "Reject", expectedVersion = 1, atMs = at } }
        }, "fork");
        var jobId = job.GetProperty("jobId").GetGuid();
        Assert.Equal("Pending", job.GetProperty("status").GetString());
        var services = new ServiceCollection();
        var options = new NpgsqlConnectionStringBuilder(replay) { Options = $"-c revenant.tenant_id={tenant}" };
        services.AddSingleton(_ => NpgsqlDataSource.Create(options.ConnectionString));
        services.AddMarrowTimeline(ObservedFinanceTimelineVersions.All, ObservedFinanceTimelineVersions.CheckpointInterval);
        services.AddSingleton<V4ReplayWorker>();
        await using var worker = services.BuildServiceProvider();
        Assert.True(await worker.GetRequiredService<V4ReplayWorker>().ProcessAsync(tenant, jobId, default));
        var (_, verified) = await Send(HttpMethod.Get, path + $"/counterfactual/{jobId}");
        Assert.Equal("Verified", verified.GetProperty("status").GetString());
        var cards = verified.GetProperty("verified").GetProperty("headlines").EnumerateArray().ToArray();
        Assert.Equal(new[] { "150000", "1", "250", "149600" }, cards.Select(x => x.GetProperty("delta").GetString()));
        var (_, cancellable) = await Send(HttpMethod.Post, path + "/counterfactual", new
        { forkPosition = "288", horizonMs = horizon, policy = "policy-v8", interventions = (object?)null }, "cancel-target");
        var cancelId = cancellable.GetProperty("jobId").GetGuid();
        var (_, cancelled) = await Send(HttpMethod.Post, path + $"/counterfactual/{cancelId}/cancel", key: "cancel");
        Assert.Equal("Cancelled", cancelled.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, cancelled.GetProperty("verified").ValueKind);
        Assert.False(await worker.GetRequiredService<V4ReplayWorker>().ProcessAsync(tenant, cancelId, default));
        var (_, second) = await Send(HttpMethod.Post, "/revenant/r8/runs", new { seed = "42" }, "second");
        Assert.NotEqual(runId, second.GetProperty("runId").GetGuid());
        Assert.Equal(first.GetProperty("manifestHash").GetString(), second.GetProperty("manifestHash").GetString());

        async Task<string> Login(string login, string role)
        {
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            await using var cmd = db.Owner.CreateCommand($"CREATE ROLE {login} LOGIN PASSWORD '{password}' IN ROLE {role}");
            await cmd.ExecuteNonQueryAsync();
            return new NpgsqlConnectionStringBuilder(db.AppConnectionString) { Username = login, Password = password }.ConnectionString;
        }

        async Task<(HttpStatusCode Status, JsonElement Body)> Send(HttpMethod method, string uri, object? body = null,
            string? key = null, string? token = null, HttpStatusCode expect = HttpStatusCode.OK)
        {
            using var message = new HttpRequestMessage(method, uri);
            message.Headers.Authorization = new("Bearer", token ?? access);
            if (body is not null) message.Content = JsonContent.Create(body);
            if (key is not null) message.Headers.Add("Idempotency-Key", key);
            using var response = await client.SendAsync(message);
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == expect, $"{method} {uri}: {response.StatusCode} {text}");
            return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
        }
    }
}
