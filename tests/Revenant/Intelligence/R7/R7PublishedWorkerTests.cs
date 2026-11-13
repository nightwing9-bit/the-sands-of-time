using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Marrow.Infrastructure.Timeline;
using Marrow.Timeline;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Revenant.Contracts;
using Revenant.Intelligence;
using Revenant.Observations;
using Revenant.Payments;
using Revenant.Synthetic;
using Revenant.Tests.Ledger;
using Revenant.Tests.Payments;
using Revenant.Timeline;
using Xunit;

namespace Revenant.Tests.Intelligence.R7;

[Collection(PaymentCollection.Name)]
public sealed class R7PublishedWorkerTests(LedgerDatabase db)
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Advisory_evidence_is_idempotent_and_rejects_conflicting_bytes()
    {
        const string tenant = PaymentKit.TenantA;
        var ct = CancellationToken.None;
        var logins = await db.ProvisionDeployedLoginsAsync();
        await using var writer = NpgsqlDataSource.Create(logins.WorkerLedger);
        var services = new ServiceCollection();
        services.AddSingleton(_ => NpgsqlDataSource.Create(logins.Worker));
        services.AddMarrowTimeline(ObservedFinanceTimelineVersions.All, ObservedFinanceTimelineVersions.CheckpointInterval);
        await using var host = services.BuildServiceProvider();
        var ledger = PaymentKit.Store(writer);
        var store = new V4SimulationStore(writer, ledger: ledger);
        var branch = new V4BranchExecution(store,
            host.GetRequiredService<ITimelineEngine<ObservedFinanceState, ObservedFinanceInput, ObservedFinanceContext>>());
        var id = new ScenarioRunId(Guid.NewGuid());
        var config = V4EpisodeConfig.Smoke;
        var plan = V4EpisodePlanner.Plan(config, config.Roots().Single(), id).Financial;
        var first = plan.Opening.Manifest.StartUtcMs + ObservedFinanceVersion.BucketMs;
        var execution = await new V4EpisodeExecutor(store, branch, ledger).ExecuteAsync(tenant, PaymentKit.Operator,
            plan with { Payments = [] }, ct, complete: false, sealThrough: first);
        var account = plan.Opening.Manifest.Accounts[0].AccountId;
        var series = V4ObservationProjector.Project(execution.History).AccountOutgoingSeries(account, first);
        var fixture = new AdvisoryEvidence(series, id, new("test-pinned-model", new string('0', 64)), "test-policy",
            AdvisoryStatus.Available, new(0, 0, 0, false, 0, .5, false, .01));
        var digest = await store.PersistAdvisoryAsync(tenant, fixture, ct);
        Assert.Equal(digest, await store.PersistAdvisoryAsync(tenant, fixture, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.PersistAdvisoryAsync(tenant, fixture with { Anomaly = fixture.Anomaly! with { RawScore = 1 } }, ct));
        var wrongTenant = await Assert.ThrowsAsync<PostgresException>(() => store.PersistAdvisoryAsync(PaymentKit.TenantB,
            fixture with { Model = fixture.Model with { ModelId = "different-test-pin" } }, ct));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, wrongTenant.SqlState);
        Assert.Equal(1L, await PaymentKit.Scalar<long>(db.Owner,
            "SELECT count(*) FROM revenant.v4_advisory_evidence WHERE execution_run_id = @run", ("run", id.Value)));
        Assert.Equal(1, (await store.ReadFinanceHistoryAsync(tenant, id, id, ct)).LastFact);
    }

    [Fact]
    [Trait("Category", "R7Longitudinal")]
    public async Task Published_worker_writes_idempotent_pinned_SSA_evidence()
    {
        const string tenant = PaymentKit.TenantA;
        var worker = Environment.GetEnvironmentVariable("REVENANT_R7_WORKER_DLL") ?? throw new InvalidOperationException("Publish Worker first.");
        var artifactRoot = Path.Combine(Path.GetDirectoryName(worker)!, "Intelligence", "Artifacts", "revenant-ssa");
        var (anomaly, anomalyZip) = ReadPin("anomaly");
        var (forecast, forecastZip) = ReadPin("forecast");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(40));
        var ct = timeout.Token;
        var logins = await db.ProvisionDeployedLoginsAsync();
        await using var writer = NpgsqlDataSource.Create(logins.WorkerLedger);
        var services = new ServiceCollection();
        services.AddSingleton(_ => NpgsqlDataSource.Create(logins.Worker));
        services.AddMarrowTimeline(ObservedFinanceTimelineVersions.All, ObservedFinanceTimelineVersions.CheckpointInterval);
        await using var host = services.BuildServiceProvider();
        var ledger = PaymentKit.Store(writer);
        var risk = new Lazy<Marrow.Kernel.Results.Result<BehavioralRiskScorerV2, BehavioralRiskFailureV2>>(() =>
            BehavioralRiskScorerV2.Load(Path.Combine(AppContext.BaseDirectory, "Intelligence", "Artifacts",
                BehavioralRiskScorerV2.ArtifactDirectory), BehavioralRiskScorerV2.FeatureSchema, BehavioralRiskScorerV2.PublishedPin));
        var store = new V4SimulationStore(writer, ledger: ledger, model: risk);
        var branch = new V4BranchExecution(store,
            host.GetRequiredService<ITimelineEngine<ObservedFinanceState, ObservedFinanceInput, ObservedFinanceContext>>());
        var id = new ScenarioRunId(Guid.NewGuid());
        // Generator 4 Pilot root 0 through bucket 1,160: past the 1,152-bucket anomaly warm-up and the 288-bucket forecast warm-up.
        var episode = V4LongitudinalPlannerG4.Plan(V4LongitudinalPlannerG4.Roots(LongitudinalSplit.Pilot).First(), id);
        var origin = episode.Financial.Opening.Manifest.StartUtcMs;
        var seal = origin + 1_160 * ObservedFinanceVersion.BucketMs;
        var plan = episode.Financial with
        {
            Payments = [.. episode.Financial.Payments.Where(p => p.Intent.EventTimeUtcMs < seal)],
        };
        var reviews = episode.ReviewTimes.Where(p => p.Value < seal).ToDictionary();
        var result = await new V4EpisodeExecutor(store, branch, ledger).ExecuteAsync(tenant, PaymentKit.Operator,
            plan, ct, ObservedRiskMode.ModelRequired, complete: false, sealThrough: seal, reviewTimes: reviews,
            anomalyModels: [anomaly], liquidityModels: [forecast]);
        var projection = V4ObservationProjector.Project(result.History);
        var account = plan.Opening.Manifest.Accounts.Select(a => projection.AccountOutgoingSeries(a.AccountId, seal))
            .OrderByDescending(a => a.Points.Take(1_152).Count(p => p.Value > 0)).First();
        Assert.True(result.History.Segments[0].Facts.Count(f => f.Input.Kind == ObservedFactKind.Screening
            && f.Input.PayloadSchema == V4ModelScreeningEvidence.Schema) > 0);

        var goodBank = await Score(seal, null);
        Assert.Equal(6, goodBank.Length);
        Assert.All(goodBank, row =>
        {
            Assert.Equal("Available", row.GetProperty("Status").GetString());
            var forecastResult = row.GetProperty("Forecast");
            Assert.Equal(8, forecastResult.GetProperty("Horizons").GetArrayLength());
            Assert.NotEmpty(row.GetProperty("EvidenceSha256").GetString()!);
        });
        var goodAccount = Assert.Single(await Score(seal, account.SubjectId));
        Assert.Equal("Available", goodAccount.GetProperty("Status").GetString());
        Assert.Equal(account.Digest, goodAccount.GetProperty("SeriesSha256").GetString());
        Assert.Equal(account.Points[^1].Value, goodAccount.GetProperty("Anomaly").GetProperty("Count").GetInt64());
        Assert.Equal(goodBank.Select(x => x.GetRawText()), (await Score(seal, null)).Select(x => x.GetRawText()));
        Assert.Equal(goodAccount.GetRawText(), Assert.Single(await Score(seal, account.SubjectId)).GetRawText());
        var early = Assert.Single(await Score(origin + ObservedFinanceVersion.BucketMs, account.SubjectId));
        Assert.Equal("InsufficientHistory", early.GetProperty("Status").GetString());

        var backup = anomalyZip + ".preserved";
        File.Move(anomalyZip, backup);
        try
        {
            var missing = Assert.Single(await Score(seal, account.SubjectId));
            Assert.Equal("ArtifactMissing", missing.GetProperty("Status").GetString());
            File.WriteAllBytes(anomalyZip, [0, 1, 2]);
            var corrupt = Assert.Single(await Score(seal, account.SubjectId));
            Assert.Equal("ArtifactInvalid", corrupt.GetProperty("Status").GetString());
        }
        finally
        {
            File.Move(backup, anomalyZip, overwrite: true);
        }
        Assert.True(File.Exists(forecastZip));
        Assert.Equal(7L, await PaymentKit.Scalar<long>(db.Owner,
            "SELECT count(*) FROM revenant.v4_advisory_evidence WHERE execution_run_id = @run", ("run", id.Value)));
        Assert.Equal(result.History.Segments[0].LastFact, (await store.ReadFinanceHistoryAsync(tenant, id, id, ct)).LastFact);

        (ObservedModelPin Pin, string Zip) ReadPin(string kind)
        {
            var directory = Assert.Single(Directory.GetDirectories(Path.Combine(artifactRoot, kind)));
            var bytes = File.ReadAllBytes(Path.Combine(directory, "manifest.json"));
            using var manifest = JsonDocument.Parse(bytes);
            var sha = manifest.RootElement.GetProperty("ArtifactSha256").GetString()!;
            var zip = Path.Combine(directory, kind + ".zip");
            Assert.Equal(sha, ObservationEncoding.Hash(File.ReadAllBytes(zip)));
            return (new($"revenant-ssa-{kind}/{ObservationEncoding.Hash(bytes)}", sha), zip);
        }

        async Task<JsonElement[]> Score(long boundary, string? subject)
        {
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var value in new[] { worker, "--v4-advisory", id.Value.ToString("D"), boundary.ToString(CultureInfo.InvariantCulture) })
                start.ArgumentList.Add(value);
            if (subject is not null) start.ArgumentList.Add(subject);
            start.Environment["ConnectionStrings__Postgres"] = logins.Worker;
            start.Environment["ConnectionStrings__RevenantLedger"] = logins.WorkerLedger;
            start.Environment["ConnectionStrings__Redis"] = "localhost:6379";
            start.Environment["Revenant__Ledger__TenantId"] = tenant;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start published Worker.");
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            try
            {
                await process.WaitForExitAsync(ct);
                var text = await stdout;
                Assert.True(process.ExitCode == 0, $"{text} {await stderr}");
                return JsonSerializer.Deserialize<JsonElement[]>(text.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1])!;
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
        }
    }
}
