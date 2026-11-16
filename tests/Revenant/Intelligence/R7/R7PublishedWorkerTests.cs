using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Hosts.Worker;
using Marrow.Infrastructure.Timeline;
using Marrow.Timeline;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Revenant.Api;
using Revenant.Contracts;
using Revenant.Cases;
using Revenant.Finance;
using Revenant.Scenario;
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
            fixture with { Model = fixture.Model! with { ModelId = "different-test-pin" } }, ct));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, wrongTenant.SqlState);
        var v2 = fixture with
        {
            Model = fixture.Model! with { ModelId = "test-pinned-model-v2" },
            Policy = SsaAdvisory.Policy,
            Anomaly = null,
            AnomalyV2 = new(0, 0, 0, SsaAlertStatistic.PoissonSurprise, 0, 3, false, false, null, 576, 6),
        };
        var v2Digest = await store.PersistAdvisoryAsync(tenant, v2, ct);
        Assert.Equal(v2Digest, await store.PersistAdvisoryAsync(tenant, v2, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.PersistAdvisoryAsync(tenant, v2 with { AnomalyV2 = v2.AnomalyV2! with { Residual = 1 } }, ct));
        Assert.Equal(1L, await PaymentKit.Scalar<long>(db.Owner,
            "SELECT count(*) FROM revenant.v4_advisory_evidence WHERE execution_run_id = @run AND schema_id = 'anomaly-evidence/2'",
            ("run", id.Value)));
        var bank = V4ObservationProjector.Project(execution.History).BankAvailableSeries("BANK-A", first);
        var baseline = AdvisoryLiquidityProjection.Project(bank, id);
        var baselineDigest = await store.PersistAdvisoryAsync(tenant, baseline, ct);
        Assert.Equal(baselineDigest, await store.PersistAdvisoryAsync(tenant, baseline, ct));
        Assert.Equal(1L, await PaymentKit.Scalar<long>(db.Owner, """
            SELECT count(*) FROM revenant.v4_advisory_evidence WHERE execution_run_id = @run
              AND schema_id = 'liquidity-projection-evidence/1' AND model_id = 'LiquidityPersistenceBaseline/1'
            """, ("run", id.Value)));
        Assert.Equal(3L, await PaymentKit.Scalar<long>(db.Owner,
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
        var anomaly = RevenantAdvisoryWorker.SupportedAnomaly;
        var anomalyZip = Path.Combine(artifactRoot, "anomaly", anomaly.ModelId.Split('/')[1], "anomaly.zip");
        Assert.Equal(anomaly.ArtifactSha256, ObservationEncoding.Hash(File.ReadAllBytes(anomalyZip)));
        var retiredPin = new ObservedModelPin("revenant-ssa-forecast/" + new string('a', 64), new string('b', 64));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
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
        // One sparse runtime fixture, not a research root. Use the existing planner's opening and
        // real scheduler/ledger for every payment and seal. Only nine intakes are needed for warmup and scoring.
        const long bucket = ObservedFinanceVersion.BucketMs;
        var config = V4EpisodeConfig.Smoke with { Hours = 97 };
        var opening = V4EpisodePlanner.Plan(config, config.Roots().Single(), id).Financial;
        var origin = opening.Opening.Manifest.StartUtcMs;
        var seal = opening.EndMs - bucket;
        var payer = opening.Opening.Manifest.Accounts[0];
        var payee = opening.Opening.Manifest.Accounts.First(a => a.BankId != payer.BankId);
        var payments = Enumerable.Range(0, 8).Select(i => Payment(i + 1, origin + (300 + i) * bucket, 100))
            .Append(Payment(9, seal - 1, 100)).Select(p => new V4PlannedPayment(p, false));
        var plan = opening with { Payments = [.. payments] };
        var result = await new V4EpisodeExecutor(store, branch, ledger).ExecuteAsync(tenant, PaymentKit.Operator,
            plan, ct, ObservedRiskMode.ModelRequired, liquidityModels: [retiredPin]);
        var projection = V4ObservationProjector.Project(result.History);
        var account = projection.AccountOutgoingSeries(payer.AccountId, seal);
        Assert.Equal(8, account.Points.Take(1_152).Count(p => p.Value > 0));
        Assert.Equal(1, account.Points[^1].Value);
        Assert.Contains(result.History.Segments[0].Facts, f => f.Input.Kind == ObservedFactKind.Screening
            && f.Input.PayloadSchema == V4ModelScreeningEvidence.Schema);

        var goodBank = await Score(seal, null);
        Assert.Equal(6, goodBank.Length);
        Assert.All(goodBank, row =>
        {
            Assert.Equal("Available", row.GetProperty("Status").GetString());
            Assert.Equal("StatisticalLiquidityProjection", row.GetProperty("Kind").GetString());
            Assert.Equal("liquidity-projection-evidence/1", row.GetProperty("Schema").GetString());
            Assert.Equal("LiquidityPersistenceBaseline/1", row.GetProperty("ProducerId").GetString());
            Assert.Equal(JsonValueKind.Null, row.GetProperty("ModelId").ValueKind);
            Assert.Equal(JsonValueKind.Null, row.GetProperty("ArtifactSha256").ValueKind);
            var liquidity = row.GetProperty("Liquidity");
            Assert.Equal("Uncalibrated", liquidity.GetProperty("Uncertainty").GetString());
            Assert.Equal(8, liquidity.GetProperty("Horizons").GetArrayLength());
            var observed = projection.Banks.Single(b => b.BankId == row.GetProperty("Subject").GetString()
                && b.Seal.BoundaryMs == seal).Balance;
            Assert.Equal(observed.Posted - observed.Reserved, liquidity.GetProperty("ObservedAvailableMinor").GetInt64());
            var h = 0;
            foreach (var step in liquidity.GetProperty("Horizons").EnumerateArray())
            {
                Assert.Equal(seal + ++h * bucket, step.GetProperty("BoundaryMs").GetInt64());
                Assert.Equal(observed.Available, step.GetProperty("ProjectedAvailableMinor").GetInt64());
            }
            Assert.False(liquidity.TryGetProperty("FirstCrossingHorizon", out _));
            Assert.NotEmpty(row.GetProperty("EvidenceSha256").GetString()!);
        });
        var goodAccount = Assert.Single(await Score(seal, account.SubjectId));
        Assert.Equal("Available", goodAccount.GetProperty("Status").GetString());
        Assert.Equal(account.Digest, goodAccount.GetProperty("SeriesSha256").GetString());
        Assert.Equal("anomaly-evidence/2", goodAccount.GetProperty("Schema").GetString());
        Assert.Equal(anomaly.ModelId, goodAccount.GetProperty("ModelId").GetString());
        Assert.Equal(anomaly.ArtifactSha256, goodAccount.GetProperty("ArtifactSha256").GetString());
        Assert.Equal(AdvisoryEvidence.GlobalAnomalyPolicy, goodAccount.GetProperty("PolicyId").GetString());
        var score = goodAccount.GetProperty("Anomaly");
        Assert.Equal(account.Points[^1].Value, score.GetProperty("Count").GetInt64());
        Assert.True(double.IsFinite(score.GetProperty("ExpectedCount").GetDouble()));
        var surprise = score.GetProperty("StatisticValue").GetDouble();
        Assert.True(double.IsFinite(surprise));
        const double threshold = 3.4120270786372;
        Assert.Equal(threshold, score.GetProperty("Threshold").GetDouble());
        Assert.Equal(surprise >= threshold, score.GetProperty("Alert").GetBoolean());
        var saved = await PaymentKit.Scalar<string>(db.Owner, """
            SELECT encode(evidence_digest, 'hex') FROM revenant.v4_advisory_evidence
            WHERE execution_run_id = @run AND schema_id = 'anomaly-evidence/2'
              AND model_id = @model AND policy_id = @policy
            """, ("run", id.Value), ("model", anomaly.ModelId), ("policy", AdvisoryEvidence.GlobalAnomalyPolicy));
        Assert.Equal(saved, goodAccount.GetProperty("EvidenceSha256").GetString());
        var evidence = RevenantAdvisoryWorker.ScoreSupported(account, id, artifactRoot);
        var decoded = R8Service.DecodeAlert(evidence.Encode(), saved, id, seal);
        Assert.Equal(AdvisoryStatus.Available, decoded.Status);
        Assert.Equal(surprise >= threshold, decoded.Alert is not null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PersistAdvisoryAsync(tenant,
            evidence with { AnomalyV2 = evidence.AnomalyV2! with { Residual = evidence.AnomalyV2.Residual + 1 } }, ct));
        Assert.Equal(goodBank.Select(x => x.GetRawText()), (await Score(seal, null)).Select(x => x.GetRawText()));
        Assert.Equal(goodAccount.GetRawText(), Assert.Single(await Score(seal, account.SubjectId)).GetRawText());
        var early = Assert.Single(await Score(origin + ObservedFinanceVersion.BucketMs, account.SubjectId));
        Assert.Equal("InsufficientHistory", early.GetProperty("Status").GetString());

        var backup = anomalyZip + ".preserved";
        File.Move(anomalyZip, backup);
        try
        {
            var missing = Assert.Single(await Score(seal, account.SubjectId));
            Unavailable(missing, "ArtifactMissing");
            File.WriteAllBytes(anomalyZip, [0, 1, 2]);
            var corrupt = Assert.Single(await Score(seal, account.SubjectId));
            Unavailable(corrupt, "ArtifactInvalid");

            // Keep the advisory ZIP corrupt while a real ModelRequired screening reserves money in a child.
            var context = result.History.Segments[0].Context;
            var childContext = new ObservedFinanceContext(context.ScenarioVersion, context.ManifestSha256, context.OriginMs,
                PolicyCatalog.RefOf(PolicyCatalog.V8), context.ReviewPolicy, context.RiskMode, context.RiskFeatureSchema,
                context.RiskModel, context.AnomalyModels, context.LiquidityModels);
            var child = new ScenarioRunId(Guid.NewGuid());
            await branch.ForkAsync(tenant, PaymentKit.Operator, plan.Opening, id, child, seal, "r7-option-a", childContext, ct);
            var inherited = await Score(seal, null, child);
            Assert.Equal(goodBank.Select(r => r.GetProperty("SeriesSha256").GetString()),
                inherited.Select(r => r.GetProperty("SeriesSha256").GetString()));
            Assert.Equal(goodBank.Select(r => r.GetProperty("Liquidity").GetRawText()),
                inherited.Select(r => r.GetProperty("Liquidity").GetRawText()));

            var payment = Payment(10, seal, ReviewPolicy.HighValueThresholdMinor, childOnly: true);
            await Dispatch(new(payment.IntentId + ":intake", V4CommandClass.Exogenous, "PaymentIntent",
                ScenarioSchemas.InputSchemaVersion, [.. PaymentIntentCodec.Instance.EncodeCanonical(payment)], seal, seal));
            var screen = await Dispatch(new(payment.IntentId + ":screen", V4CommandClass.DerivedOpportunity, "Screening",
                "v4-payment-opportunity/1", [.. new Marrow.Kernel.Canonical.CanonicalWriter().WriteInt32(1)
                    .WriteString(payment.IntentId).ToArray()], seal, seal));
            Assert.Equal(nameof(PaymentStatus.Held), screen.OutcomeCode);
            Assert.Equal(V4StepKind.Sealed, (await store.SealAsync(tenant, child, opening.EndMs, ct)).Kind);
            var childHistory = await branch.ReadProjectionHistoryAsync(tenant, child, ct);
            var screening = Assert.Single(childHistory.Segments[^1].Facts, f => f.Input.Kind == ObservedFactKind.Screening);
            Assert.Equal(BehavioralRiskScorerV2.PublishedPin, V4ModelScreeningEvidence.Decode(screening.Input).Model);
            Assert.Null(await ledger.GetPaymentAsync(tenant, id, payment.IntentId, ct));
            var sourceNext = await Score(opening.EndMs, null);
            var childNext = await Score(opening.EndMs, null, child);
            long Available(JsonElement[] rows) => rows.Single(r => r.GetProperty("Subject").GetString() == payer.BankId)
                .GetProperty("Liquidity").GetProperty("ObservedAvailableMinor").GetInt64();
            Assert.Equal(Available(sourceNext) - payment.Amount.MinorUnits, Available(childNext));

            async Task<V4ScheduledCommand> Dispatch(V4CommandRequest command)
            {
                await store.AdmitAsync(tenant, child, command, ct);
                var step = await store.AdvanceOneStepAsync(tenant, child, ct);
                Assert.Equal(V4StepKind.ProcessedCommand, step.Kind);
                Assert.NotNull(step.Command);
                Assert.Equal(V4CommandStatus.Completed, step.Command.Status);
                return step.Command;
            }
        }
        finally
        {
            File.Move(backup, anomalyZip, overwrite: true);
        }
        Assert.Equal(13L, await PaymentKit.Scalar<long>(db.Owner,
            "SELECT count(*) FROM revenant.v4_advisory_evidence WHERE execution_run_id = @run", ("run", id.Value)));
        Assert.Equal(result.History.Segments[0].LastFact, (await store.ReadFinanceHistoryAsync(tenant, id, id, ct)).LastFact);

        PaymentIntent Payment(int sequence, long at, long amount, bool childOnly = false) =>
            new(id, $"r7-runtime-{sequence}", sequence, at, payer.BankId, payer.AccountId, payee.BankId, payee.AccountId,
                Money.Usd(amount), SyntheticTruth.Legitimate, new(childOnly, false));

        static void Unavailable(JsonElement receipt, string expected)
        {
            Assert.Equal(expected, receipt.GetProperty("Status").GetString());
            Assert.Equal(JsonValueKind.Null, receipt.GetProperty("EvidenceSha256").ValueKind);
            Assert.Equal(JsonValueKind.Null, receipt.GetProperty("Anomaly").ValueKind);
        }

        async Task<JsonElement[]> Score(long boundary, string? subject, ScenarioRunId? execution = null)
        {
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var value in new[] { worker, "--v4-advisory", (execution ?? id).Value.ToString("D"), boundary.ToString(CultureInfo.InvariantCulture) })
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
