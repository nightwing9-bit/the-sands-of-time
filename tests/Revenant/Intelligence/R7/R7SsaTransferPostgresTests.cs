using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Hosts.Worker;
using Marrow.Infrastructure.Timeline;
using Marrow.Kernel.Canonical;
using Marrow.Timeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ML;
using Microsoft.ML.Transforms.TimeSeries;
using Npgsql;
using Revenant.Cases;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Intelligence;
using Revenant.Observations;
using Revenant.Payments;
using Revenant.Scenario;
using Revenant.Synthetic;
using Revenant.Tests.Ledger;
using Revenant.Tests.Payments;
using Revenant.Timeline;
using Xunit;

namespace Revenant.Tests.Intelligence.R7;

[Collection(PaymentCollection.Name)]
public sealed class R7SsaTransferPostgresTests(LedgerDatabase db)
{
    private const string Tenant = PaymentKit.TenantA;
    private const string Owner = PaymentKit.Operator;
    private const long Bucket = ObservedFinanceVersion.BucketMs;
    private const int Training = 96;
    private const int Warmup = 48;
    private const int Duration = 128;
    private const int Shift = 96;
    private const int Horizon = V4BankTargets.HorizonCount;
    private const long Scale = 1_024;
    private static readonly int[] SpikeBuckets = [82, 110];

    [Fact]
    [Trait("Category", "R7SsaTransfer")]
    public async Task Frozen_SSA_models_transfer_across_independent_committed_histories()
    {
        Assert.True(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64);
        var destination = Environment.GetEnvironmentVariable("REVENANT_R7_TRANSFER_OUTPUT")
            ?? throw new InvalidOperationException("Set the transfer evidence directory.");
        Directory.CreateDirectory(destination);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var ct = timeout.Token;
        var timer = Stopwatch.StartNew();
        var logins = await db.ProvisionDeployedLoginsAsync();
        await using var writer = NpgsqlDataSource.Create(logins.WorkerLedger);
        await using var host = TimelineHost(logins.Worker);
        var ledger = PaymentKit.Store(writer);
        var store = new V4SimulationStore(writer, ledger: ledger);
        var branch = new V4BranchExecution(store, Engine(host));
        var executor = new V4EpisodeExecutor(store, branch, ledger);

        var roots = new List<(V4EpisodePlan Plan, V4ObservationProjector Projection, V4EpisodeTiming Timing)>();
        for (var index = 0; index < 3; index++)
        {
            var plan = CreatePlan(index);
            var executed = await executor.ExecuteAsync(Tenant, Owner, plan, ct);
            var projection = V4ObservationProjector.Project(executed.History);
            Assert.Equal(Duration * BehavioralEpisodeScenario.BankCount, projection.Banks.Count);
            Assert.Equal(plan.Payments.Length, projection.Banks.Sum(b => b.Intents));
            roots.Add((plan, projection, executed.Timing));
        }
        var (train, unseenA, unseenB) = (roots[0], roots[1], roots[2]);
        var account = train.Plan.Opening.Manifest.Accounts[0];
        var trainBoundary = train.Plan.Opening.Manifest.StartUtcMs + Training * Bucket;
        var trainAccount = train.Projection.AccountOutgoingSeries(account.AccountId, trainBoundary);
        var trainBank = train.Projection.BankAvailableSeries(account.BankId, trainBoundary);
        Assert.Equal(Training, trainAccount.Points.Length);
        Assert.Equal(Training, trainBank.Points.Length);
        Assert.True(trainAccount.Points.Count(p => p.Value > 0) > Training / 2);
        Assert.True(trainBank.Points.Select(p => p.Value).Distinct().Count() > Training / 2);
        var ml = new MLContext(seed: 7);
        var spikePath = Path.Combine(destination, "transfer-anomaly.zip");
        var forecastPath = Path.Combine(destination, "transfer-forecast.zip");
        Fit(ml, trainAccount, trainBank, spikePath, forecastPath);
        var spikeHash = FileHash(spikePath);
        var forecastHash = FileHash(forecastPath);

        var a = Analyze(unseenA.Plan, unseenA.Projection, ml, spikePath, forecastPath);
        var b = Analyze(unseenB.Plan, unseenB.Projection, ml, spikePath, forecastPath);
        Assert.NotEqual(a.OpeningAvailable, b.OpeningAvailable);
        Assert.NotEqual(a.AccountDigest, b.AccountDigest);
        Assert.NotEqual(a.BankDigest, b.BankDigest);
        Assert.NotEqual(a.NonzeroBuckets, b.NonzeroBuckets);
        Assert.True(a.DetectedSpikes > 0 && a.AlertedNonzeroBuckets < a.ScoredNonzeroBuckets);
        Assert.True(b.DetectedSpikes > 0 && b.AlertedNonzeroBuckets < b.ScoredNonzeroBuckets);
        Assert.Equal(a, Analyze(unseenA.Plan, unseenA.Projection, ml, spikePath, forecastPath));

        // Restore through the verified Marrow fork. A large legal reservation at the same timestamp as
        // the inherited seal can change forecasts only after the next completed seal.
        var (forkPlan, source, _) = unseenA;
        var origin = forkPlan.Opening.Manifest.StartUtcMs;
        var forkBoundary = origin + 112 * Bucket;
        var childId = new ScenarioRunId(new Guid("5237ffff-0000-0000-0000-000000000000"));
        var run = forkPlan.Opening;
        var childContext = new ObservedFinanceContext(run.Manifest.ScenarioVersion, run.Manifest.ContentHash, origin,
            PolicyCatalog.RefOf(PolicyCatalog.V8), ReviewPolicy.Version, ObservedRiskMode.NotRequired, "", null, [], []);
        await branch.ForkAsync(Tenant, Owner, run, run.RunId, childId, forkBoundary, "r7-transfer", childContext, ct);
        var payer = run.Manifest.Accounts[0];
        var receiver = run.Manifest.Accounts[2];
        var sequence = forkPlan.Payments.Count(p => p.Intent.EventTimeUtcMs < forkBoundary) + 1;
        var pending = new PaymentIntent(run.RunId, "r7-fork-reservation", sequence, forkBoundary, payer.BankId, payer.AccountId,
            receiver.BankId, receiver.AccountId, Money.Usd(175_000), SyntheticTruth.Legitimate, new(true, false));
        await Dispatch(new("r7-fork-intake", V4CommandClass.Exogenous, nameof(ObservedFactKind.PaymentIntent),
            ScenarioSchemas.InputSchemaVersion, [.. PaymentIntentCodec.Instance.EncodeCanonical(pending)], forkBoundary, forkBoundary),
            nameof(PaymentStatus.Initiated));
        await Dispatch(new("r7-fork-screen", V4CommandClass.DerivedOpportunity, nameof(ObservedFactKind.Screening),
            "v4-payment-opportunity/1", [.. new CanonicalWriter().WriteInt32(1).WriteString(pending.IntentId).ToArray()],
            forkBoundary, forkBoundary), nameof(PaymentStatus.Held));
        var nextBoundary = forkBoundary + Bucket;
        Assert.Equal(V4StepKind.Sealed, (await store.SealAsync(Tenant, childId, nextBoundary, ct)).Kind);
        Assert.Equal(HistoryStatus.Verified, (await branch.CompleteAsync(Tenant, childId, ct)).Status);
        await using var coldHost = TimelineHost(logins.Worker);
        var cold = new V4BranchExecution(store, Engine(coldHost));
        var child = V4ObservationProjector.Project(await cold.ReadProjectionHistoryAsync(Tenant, childId, ct));
        var coldSource = V4ObservationProjector.Project(await cold.ReadProjectionHistoryAsync(Tenant, run.RunId, ct));
        var sourceBankAtFork = source.BankAvailableSeries(payer.BankId, forkBoundary);
        Assert.Equal(sourceBankAtFork.Encode(), coldSource.BankAvailableSeries(payer.BankId, forkBoundary).Encode());
        var childBankAtFork = child.BankAvailableSeries(payer.BankId, forkBoundary);
        Assert.Equal(sourceBankAtFork.Encode(), childBankAtFork.Encode());
        Assert.Equal(Forecast(ml, forecastPath, sourceBankAtFork), Forecast(ml, forecastPath, childBankAtFork));
        var sourceAccountAtFork = source.AccountOutgoingSeries(payer.AccountId, forkBoundary);
        var childAccountAtFork = child.AccountOutgoingSeries(payer.AccountId, forkBoundary);
        Assert.Equal(sourceAccountAtFork.Encode(), childAccountAtFork.Encode());
        Assert.Equal(Spike(ml, spikePath, sourceAccountAtFork), Spike(ml, spikePath, childAccountAtFork));
        var childRow = child.Banks.Single(r => r.BankId == payer.BankId && r.Seal.BoundaryMs == nextBoundary);
        Assert.Equal(pending.Amount.MinorUnits, childRow.Reserved);
        Assert.Equal(sourceBankAtFork.Points[^1].Value - pending.Amount.MinorUnits, childRow.Balance.Available);
        var sourceNext = coldSource.BankAvailableSeries(payer.BankId, nextBoundary);
        var childNext = child.BankAvailableSeries(payer.BankId, nextBoundary);
        Assert.NotEqual(sourceNext.Points[^1].Value, childNext.Points[^1].Value);
        Assert.False(Forecast(ml, forecastPath, sourceNext).SequenceEqual(Forecast(ml, forecastPath, childNext)));
        Assert.Equal(spikeHash, FileHash(spikePath));
        Assert.Equal(forecastHash, FileHash(forecastPath));

        using var process = Process.GetCurrentProcess();
        File.WriteAllBytes(Path.Combine(destination, "transfer.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            Platform = RuntimeInformation.RuntimeIdentifier,
            Framework = RuntimeInformation.FrameworkDescription,
            MlNet = typeof(TimeSeriesPredictionEngine<SeriesPoint, SpikeOutput>).Assembly.GetName().Version?.ToString(),
            TransferWarmup = Warmup, TrainingBuckets = Training, ScaleMinor = Scale,
            FittedAccountSeriesSha256 = trainAccount.Digest, FittedBankSeriesSha256 = trainBank.Digest,
            SpikeZipSha256 = spikeHash, ForecastZipSha256 = forecastHash,
            Unseen = new[] { a, b }, ElapsedMs = timer.Elapsed.TotalMilliseconds, process.PeakWorkingSet64,
            ExecutionMs = roots.Select(r => r.Timing.Total.TotalMilliseconds),
            Branch = new
            {
                Source = run.RunId, Child = childId, ForkBoundary = forkBoundary, AvailableAtFork = sourceBankAtFork.Points[^1].Value,
                ChildReserved = childRow.Reserved, SourceAvailable = sourceNext.Points[^1].Value, ChildAvailable = childNext.Points[^1].Value,
                InheritedForecast = Forecast(ml, forecastPath, sourceBankAtFork),
                SourceForecast = Forecast(ml, forecastPath, sourceNext), ChildForecast = Forecast(ml, forecastPath, childNext),
            },
        }, new JsonSerializerOptions { WriteIndented = true }));

        async Task Dispatch(V4CommandRequest request, string outcome)
        {
            await store.AdmitAsync(Tenant, childId, request, ct);
            var step = await store.AdvanceOneStepAsync(Tenant, childId, ct);
            Assert.Equal(V4StepKind.ProcessedCommand, step.Kind);
            Assert.Equal(outcome, step.Command!.OutcomeCode);
        }
    }

    private static void Fit(MLContext ml, V4AdvisorySeries account, V4AdvisorySeries bank, string spikePath, string forecastPath)
    {
        var velocity = ml.Data.LoadFromEnumerable(account.Points.Select(p => new SeriesPoint { Value = p.Value }));
        var spike = ml.Transforms.DetectSpikeBySsa(nameof(SpikeOutput.Prediction), nameof(SeriesPoint.Value),
            confidence: 95d, pvalueHistoryLength: 16, trainingWindowSize: 32, seasonalityWindowSize: 8, side: AnomalySide.Positive);
        ml.Model.Save(spike.Fit(velocity), velocity.Schema, spikePath);

        var normalize = new AdvisoryBalanceNormalization(bank.ReferenceMinor!.Value, Scale);
        var balance = ml.Data.LoadFromEnumerable(bank.Points.Select(p => new SeriesPoint { Value = normalize.Normalize(p.Value) }));
        var forecast = ml.Forecasting.ForecastBySsa(nameof(ForecastOutput.Forecast), nameof(SeriesPoint.Value),
            windowSize: 8, seriesLength: 32, trainSize: Training, horizon: Horizon, isAdaptive: false, confidenceLevel: 0.95f,
            confidenceLowerBoundColumn: nameof(ForecastOutput.LowerBound), confidenceUpperBoundColumn: nameof(ForecastOutput.UpperBound));
        ml.Model.Save(forecast.Fit(balance), balance.Schema, forecastPath);
    }

    private static TimeSeriesPredictionEngine<SeriesPoint, T> Load<T>(MLContext ml, string path) where T : class, new() =>
        ml.Model.Load(path, out _).CreateTimeSeriesEngine<SeriesPoint, T>(ml);

    private static double[] Spike(MLContext ml, string path, V4AdvisorySeries series)
    {
        using var engine = Load<SpikeOutput>(ml, path);
        double[] last = [];
        foreach (var point in series.Points) last = Observe(engine, point.Value);
        return last;
    }

    private static double[] Observe(TimeSeriesPredictionEngine<SeriesPoint, SpikeOutput> engine, long count)
    {
        var result = engine.Predict(new SeriesPoint { Value = count }).Prediction;
        Assert.Equal(3, result.Length);
        Assert.True(result.All(double.IsFinite));
        Assert.Contains(result[0], new[] { 0d, 1d });
        Assert.InRange(result[2], 0d, 1d);
        return result;
    }

    private static long[] Forecast(MLContext ml, string path, V4AdvisorySeries series)
    {
        using var engine = Load<ForecastOutput>(ml, path);
        var normalize = new AdvisoryBalanceNormalization(series.ReferenceMinor!.Value, Scale);
        foreach (var point in series.Points)
            _ = engine.Predict(new SeriesPoint { Value = normalize.Normalize(point.Value) });
        return Restore(engine.Predict(), normalize);
    }

    private static long[] Restore(ForecastOutput output, AdvisoryBalanceNormalization normalize)
    {
        Assert.Equal(Horizon, output.Forecast.Length);
        Assert.Equal(Horizon, output.LowerBound.Length);
        Assert.Equal(Horizon, output.UpperBound.Length);
        for (var h = 0; h < Horizon; h++)
        {
            Assert.True(float.IsFinite(output.Forecast[h]) && float.IsFinite(output.LowerBound[h]) && float.IsFinite(output.UpperBound[h]));
            Assert.InRange(output.Forecast[h], output.LowerBound[h], output.UpperBound[h]);
        }
        return output.Forecast.Select(normalize.Restore).ToArray();
    }

    private sealed record TransferResult(string AccountDigest, string BankDigest, long OpeningAvailable,
        int NonzeroBuckets, int ScoredNonzeroBuckets, int AlertedNonzeroBuckets, int AlertsDuringWarmup, int Alerts,
        int DetectedSpikes, int BaselineAlerts, int BaselineDetectedSpikes, int AlertsAfterShift,
        int ForecastOrigins, double SsaMae, double PersistenceMae, double NominalIntervalCoverage, long MaxNormalizationErrorMinor,
        double FirstFittedForecastBeforeObservations, double FirstForecastAfterWarmup, string RollingPredictionsSha256);

    private static TransferResult Analyze(V4EpisodePlan plan, V4ObservationProjector projector, MLContext ml, string spikePath, string forecastPath)
    {
        var subject = plan.Opening.Manifest.Accounts[0];
        var account = projector.AccountOutgoingSeries(subject.AccountId, plan.EndMs);
        var bank = projector.BankAvailableSeries(subject.BankId, plan.EndMs);
        var bankTargets = V4BankTargets.Build(projector.Banks).Where(t => t.BankId == subject.BankId).ToArray();
        var normalize = new AdvisoryBalanceNormalization(bank.ReferenceMinor!.Value, Scale);
        using var detector = Load<SpikeOutput>(ml, spikePath);
        using var forecaster = Load<ForecastOutput>(ml, forecastPath);
        var fittedForecast = forecaster.Predict().Forecast[0];
        var warmedForecast = 0d;
        var forecasts = 0;
        var targetCount = 0;
        var ssaError = 0d;
        var persistenceError = 0d;
        var alerts = 0;
        var detectedSpikes = 0;
        var baselineAlerts = 0;
        var baselineDetectedSpikes = 0;
        var alertsAfterShift = 0;
        var alertsDuringWarmup = 0;
        var scoredNonzero = 0;
        var alertedNonzero = 0;
        var covered = 0;
        var digest = ObservationEncoding.Begin("r7-transfer-rolling-predictions/1");
        double[] last = [];
        long[] prediction = [];
        for (var i = 0; i < account.Points.Length; i++)
        {
            var current = account.Points[i].Value;
            last = Observe(detector, current);
            var available = bank.Points[i].Value;
            var updated = forecaster.Predict(new SeriesPoint { Value = normalize.Normalize(available) });
            if (i < Warmup)
            {
                if (last[0] == 1) alertsDuringWarmup++;
                continue;
            }
            if (current > 0) scoredNonzero++;
            var spike = SpikeBuckets.Contains(i);
            if (last[0] == 1)
            {
                alerts++;
                if (current > 0) alertedNonzero++;
                if (spike) detectedSpikes++;
                if (i >= Shift) alertsAfterShift++;
            }
            var recent = account.Points.Skip(i - Warmup).Take(Warmup).Select(p => p.Value).Order().ToArray();
            if (current > recent[(int)Math.Ceiling(0.95 * Warmup) - 1])
            {
                baselineAlerts++;
                if (spike) baselineDetectedSpikes++;
            }

            // Read the current state before any future input and verify the read does not advance it.
            var forecastOnly = forecaster.Predict();
            prediction = Restore(forecastOnly, normalize);
            AssertSameForecast(forecastOnly, updated);
            AssertSameForecast(forecastOnly, forecaster.Predict());
            digest.WriteInt64(bank.Points[i].Seal.BoundaryMs);
            foreach (var value in last) digest.WriteInt64(BitConverter.DoubleToInt64Bits(value));
            foreach (var value in forecastOnly.Forecast.Concat(forecastOnly.LowerBound).Concat(forecastOnly.UpperBound))
                digest.WriteInt32(BitConverter.SingleToInt32Bits(value));
            if (i == Warmup) warmedForecast = forecastOnly.Forecast[0];
            var target = bankTargets[i];
            Assert.Equal(bank.Points[i].Seal, target.Origin);
            if (!target.FullyCovered) continue;
            forecasts++;
            for (var h = 0; h < Horizon; h++)
            {
                var actual = target.Horizons[h]!.Balance.Available;
                targetCount++;
                ssaError += Math.Abs((double)prediction[h] - actual);
                persistenceError += Math.Abs((double)available - actual);
                if (actual >= normalize.Restore(forecastOnly.LowerBound[h])
                    && actual <= normalize.Restore(forecastOnly.UpperBound[h])) covered++;
            }
        }
        Assert.Equal(last, Spike(ml, spikePath, account));
        Assert.Equal(prediction, Forecast(ml, forecastPath, bank));
        Assert.True(forecasts > 0 && persistenceError > 0);
        var precision = bank.Points.Max(p => Math.Abs(normalize.Restore(normalize.Normalize(p.Value)) - p.Value));
        return new(account.Digest, bank.Digest, bank.ReferenceMinor!.Value, account.Points.Count(p => p.Value > 0),
            scoredNonzero, alertedNonzero, alertsDuringWarmup, alerts, detectedSpikes, baselineAlerts, baselineDetectedSpikes,
            alertsAfterShift, forecasts, ssaError / targetCount, persistenceError / targetCount, (double)covered / targetCount,
            precision, fittedForecast, warmedForecast, ObservationEncoding.Hash(digest.ToArray()));
    }

    private static void AssertSameForecast(ForecastOutput expected, ForecastOutput actual)
    {
        Assert.Equal(expected.Forecast.Select(BitConverter.SingleToInt32Bits), actual.Forecast.Select(BitConverter.SingleToInt32Bits));
        Assert.Equal(expected.LowerBound.Select(BitConverter.SingleToInt32Bits), actual.LowerBound.Select(BitConverter.SingleToInt32Bits));
        Assert.Equal(expected.UpperBound.Select(BitConverter.SingleToInt32Bits), actual.UpperBound.Select(BitConverter.SingleToInt32Bits));
    }

    private static V4EpisodePlan CreatePlan(int root)
    {
        var id = new ScenarioRunId(new Guid(0x52370000 + root, 0, 0, new byte[8]));
        var origin = V4EpisodeConfig.Qualification.OriginMs + root * 86_400_000L;
        var run = BehavioralEpisodeScenario.Create(id, 700_101 + 123 * root, origin, Duration * Bucket, [2, 2, 2, 2, 2, 2]);
        var a = run.Manifest.Accounts[0];
        var b = run.Manifest.Accounts[2];
        var rhythm = root switch
        {
            0 => new[] { 2, 1, 1, 3, 2, 1, 0, 1 },
            1 => [2, 1, 0, 3, 1, 1, 0, 1],
            _ => [2, 1, 1, 3, 1, 2, 1, 1],
        };
        var payments = new List<V4PlannedPayment>();
        for (var i = 0; i < Duration; i++)
        {
            var phase = i % rhythm.Length;
            var count = rhythm[phase];
            if (root > 0 && i >= Shift && phase == 6) count++;
            if (root > 0 && SpikeBuckets.Contains(i)) count += 16;
            for (var j = 0; j < count; j++) Add(a, b, 901 + 127 * phase + 53 * root + j, i, j);
            // Clear interbank payments in both directions move real bank liquidity in a repeatable short cycle.
            Add(b, a, phase < 4 ? 8_071 + 191 * root : 17_043 + 223 * root, i, count);
            var from = run.Manifest.Accounts[4 + 2 * (i % 4)];
            var to = run.Manifest.Accounts[4 + 2 * ((i + 1) % 4)];
            Add(from, to, 400 + 79 * phase, i, count + 1);
        }
        return new(run, origin + Duration * Bucket, [.. payments]);

        void Add(AccountDescriptor from, AccountDescriptor to, long amount, int bucket, int slot)
        {
            var sequence = payments.Count + 1;
            var at = origin + bucket * Bucket + 1_000 + 1_000 * slot;
            var intent = new PaymentIntent(id, $"P-{sequence:D8}", sequence, at, from.BankId, from.AccountId,
                to.BankId, to.AccountId, Money.Usd(amount), SyntheticTruth.Legitimate, new(false, false));
            payments.Add(new(intent, false));
        }
    }

    private static string FileHash(string path) => ObservationEncoding.Hash(File.ReadAllBytes(path));

    private static ServiceProvider TimelineHost(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddMarrowTimeline(ObservedFinanceTimelineVersions.All, ObservedFinanceTimelineVersions.CheckpointInterval);
        return services.BuildServiceProvider();
    }

    private static ITimelineEngine<ObservedFinanceState, ObservedFinanceInput, ObservedFinanceContext> Engine(ServiceProvider services) =>
        services.GetRequiredService<ITimelineEngine<ObservedFinanceState, ObservedFinanceInput, ObservedFinanceContext>>();
}
