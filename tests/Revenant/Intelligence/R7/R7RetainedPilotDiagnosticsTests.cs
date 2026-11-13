using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hosts.Worker;
using Microsoft.ML;
using Revenant.Intelligence;
using Revenant.Observations;
using Revenant.Synthetic;
using Xunit;

namespace Revenant.Tests.Intelligence.R7;

/// <summary>
/// Bounded scientific diagnostics on the retained generator-3 pilot streams (run 43363691834). They read frozen evidence and
/// write a report; they never promote an artifact. Managed diagnostics run on any host. Native ones need Linux AMD64.
/// </summary>
public sealed class R7RetainedPilotDiagnosticsTests
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, IncludeFields = true, Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    [Fact]
    [Trait("Category", "R7Diagnostics")]
    public void Measure_label_identifiability_and_linear_predictability_of_the_retained_pilot()
    {
        var (input, output) = Paths();
        var roots = Pilot(input);
        var events = R7Diagnostics.Events(roots);
        var eligible = events.Where(e => e.PriorHistory).ToArray();
        bool Anomaly(R7Diagnostics.EventProfile e) => e.Kind != LongitudinalEventKind.LegitimateBurst;

        var forecast = roots.Select((root, index) => (root, index)).Select(pair =>
        {
            var (root, index) = pair;
            return root.Banks.Select(bank =>
            {
                var opening = bank.ReferenceMinor!.Value;
                var levels = bank.Points.Select(p => p.Value).ToArray();
                var deltas = R7Diagnostics.Changes(opening, levels, SsaAdvisory.Scale(opening, 1_000));
                return new
                {
                    Root = index, bank.SubjectId, NonzeroChangeFraction = deltas.Count(d => d != 0) / (double)deltas.Length,
                    ChangeAutocorrelation = R7Diagnostics.Autocorrelation(deltas, [1, 2, 3, 4, 6, 8, 12, 24, 288]),
                    LevelAutocorrelation = R7Diagnostics.Autocorrelation(levels.Select(v => (double)v).ToArray(), [1, 8, 288]),
                };
            });
        }).SelectMany(x => x).ToArray();

        // Train a least-squares AR on root 0 changes and test on root 1, and an in-sample oracle fitted on root 1 itself.
        var linear = new[] { 8, 24 }.Select(order =>
        {
            var train = roots[0].Banks.Select(b => R7Diagnostics.Changes(b.ReferenceMinor!.Value,
                b.Points.Select(p => p.Value).ToArray(), SsaAdvisory.Scale(b.ReferenceMinor!.Value, 1_000)));
            var test = roots[1].Banks.Select(b => R7Diagnostics.Changes(b.ReferenceMinor!.Value,
                b.Points.Select(p => p.Value).ToArray(), SsaAdvisory.Scale(b.ReferenceMinor!.Value, 1_000)));
            var transfer = R7Diagnostics.FitAutoregression(train, order, 1);
            var oracle = R7Diagnostics.FitAutoregression(test, order, 1);
            return new
            {
                Order = order,
                Transfer = Summaries(R7ForecastMetrics.Evaluate(ArCases(roots[1], transfer), new long[8])),
                InSampleOracle = Summaries(R7ForecastMetrics.Evaluate(ArCases(roots[1], oracle), new long[8])),
            };
        }).ToArray();

        Write(output, "pilot-identifiability-and-linear-predictability.json", new
        {
            Source = "TestResults/r7/pilot-43363691834 (generator 3)",
            EventCount = events.Length, EligibleEventCount = eligible.Length,
            Separability = R7Diagnostics.Separability(eligible, Anomaly, e => !Anomaly(e)),
            ByKind = eligible.GroupBy(e => e.Kind).Select(g => new
            {
                Kind = g.Key, Count = g.Count(), MeanMax = g.Average(e => e.Max), MeanTotal = g.Average(e => e.Total),
                MeanDuration = g.Average(e => e.Duration), MeanSeasonalExcess = g.Average(e => e.SeasonalExcess),
                MeanPriorMean = g.Average(e => e.PriorMeanCount),
            }).ToArray(),
            DailyProfilePersistence = roots.SelectMany(r => r.Accounts).Select(a => new
            {
                a.SubjectId, Correlation = R7Diagnostics.DailyProfilePersistence(a.Points.Select(p => p.Value).ToArray()),
            }).ToArray(),
            Events = events, Banks = forecast, Linear = linear,
        });
    }

    [Fact]
    [Trait("Category", "R7DiagnosticsNative")]
    public void Trace_native_p_values_state_memory_and_one_factor_forecast_ablations_on_the_retained_pilot()
    {
        Assert.True(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64);
        var (input, output) = Paths();
        var roots = Pilot(input);
        var (train, unseen) = (roots[0], roots[1]);
        var ml = new MLContext(seed: 7);
        var trainingAccount = train.Accounts.Where(R7LegacyV3Procedures.Eligible)
            .OrderByDescending(s => s.Points.Take(288).Count(p => p.Value > 0)).ThenBy(s => s.SubjectId, StringComparer.Ordinal).First();
        var trainingCounts = trainingAccount.Points.Select(p => p.Value).ToArray();

        // 1. Native p-value validity by history length, and the accumulator mechanism behind the nonfinite values.
        var pvalue = new[] { 1, 8, 16, 32, 64 }.Select(history =>
        {
            var spec = new SsaAnomalySpec(SsaCountTransform.Count, 8, 288, history, 288, 8);
            var artifact = RevenantSsaInference.FitAnomaly(ml, spec, trainingCounts, 0);
            var policy = new SsaAlertPolicy(SsaAlertStatistic.Residual, 1e9, 0);
            var accounts = unseen.Accounts.Select(a =>
            {
                var counts = a.Points.Select(p => p.Value).ToArray();
                var steps = RevenantSsaInference.ScoreAccount(ml, artifact, spec, policy, counts);
                var replica = new R7Diagnostics.KernelPValueReplica(history);
                var replicated = steps.Select(s => replica.Next(s.Residual)).ToArray();
                var nonfinite = steps.Select((s, i) => (s, i)).Where(x => x.s.NativePValue is null).Select(x => x.i).ToArray();
                return new
                {
                    a.SubjectId, Scored = steps.Length, Nonfinite = nonfinite.Length,
                    NonfiniteWithZeroDispersionWindow = nonfinite.Count(i => i >= history
                        && steps.Skip(i - history).Take(history + 1).Select(s => (float)s.Residual).Distinct().Count() == 1),
                    ReplicaNonfinite = replicated.Count(p => !double.IsFinite(p)),
                    ReplicaAgreement = steps.Length == 0 ? (double?)null
                        : steps.Zip(replicated).Count(x => (x.First.NativePValue is null) == !double.IsFinite(x.Second)) / (double)steps.Length,
                    FirstNonfinite = nonfinite.Length == 0 ? null : new
                    {
                        Bucket = steps[nonfinite[0]].Bucket, a.Digest,
                        Counts = steps.Skip(Math.Max(0, nonfinite[0] - history - 2)).Take(history + 3).Select(s => s.Count).ToArray(),
                        Residuals = steps.Skip(Math.Max(0, nonfinite[0] - history - 2)).Take(history + 3).Select(s => s.Residual).ToArray(),
                        NativePValues = steps.Skip(Math.Max(0, nonfinite[0] - history - 2)).Take(history + 3).Select(s => s.NativePValue).ToArray(),
                    },
                    ResidualEventAuc = ResidualAuc(steps, unseen.Events.Where(e => e.Subject == a.SubjectId).ToArray()),
                };
            }).ToArray();
            return new { History = history, ArtifactSha256 = ObservationEncoding.Hash(artifact), Accounts = accounts };
        }).ToArray();

        // 2. How long the training-series state survives in a freshly loaded engine.
        var memory = new[] { 8, 24, 289 }.Select(window =>
        {
            var training = Math.Max(2 * window + 1, 288);
            var spec = new SsaAnomalySpec(SsaCountTransform.AnscombeZeroAnchored, window, training, 16,
                SsaAnomalySpec.StateMemory(window), 1);
            var artifact = RevenantSsaInference.FitAnomaly(ml, spec, trainingCounts, 0);
            var subject = unseen.Accounts.OrderByDescending(a => a.Points.Sum(p => p.Value)).First().Points.Select(p => p.Value).ToArray();
            var a = Residuals(ml, artifact, spec, subject);
            var b = Residuals(ml, artifact, spec, [.. Enumerable.Repeat(3L, 3 * window), .. subject]);
            var lastDifference = Enumerable.Range(0, subject.Length).LastOrDefault(i => Math.Abs(a[i] - b[i + 3 * window]) > 1e-6, -1);
            return new { Window = window, PredictedMemory = SsaAnomalySpec.StateMemory(window), LastDifferentBucket = lastDifference };
        }).ToArray();

        // 3. One-factor forecast ablations at identical origins (warm-up 288, every bank of the unseen root).
        var trainingBank = train.Banks.OrderByDescending(s => s.Points.Take(288).Select(p => p.Value).Distinct().Count())
            .ThenBy(s => s.SubjectId, StringComparer.Ordinal).First();
        var level = SsaForecastRepresentation.OpeningCenteredLevel;
        var change = SsaForecastRepresentation.CompletedIntervalChange;
        var ablations = new (string Name, string Factor, SsaForecastSpec Spec)[]
        {
            ("L0-original", "baseline: level, w8, n32, train first 288, exact rank, scale 1024-equivalent", new(level, 8, 32, 288, null, 288, 1)),
            ("L1-rank2", "rank only", new(level, 8, 32, 288, 2, 288, 1)),
            ("L2-train2016", "training history only", new(level, 8, 32, 2016, null, 288, 1)),
            ("D0-change", "representation only", new(change, 8, 32, 288, null, 288, 1)),
            ("D1-change-train2016", "representation + full training history", new(change, 8, 32, 2016, null, 288, 1)),
            ("D2-change-w24-train2016", "window 24 on D1", new(change, 24, 96, 2016, null, 288, 1)),
            ("D3-change-w24-rank4-train2016", "fixed rank 4 on D2", new(change, 24, 96, 2016, 4, 288, 1)),
        };
        var trainingLevels = trainingBank.Points.Select(p => p.Value).ToArray();
        var forecasts = ablations.Select(a =>
        {
            // ScaleDivisor 1 with a bank-specific scale would change L0; the historical scale was a fixed 1,024 minor units.
            var spec = a.Spec with { ScaleDivisor = Math.Max(1, trainingBank.ReferenceMinor!.Value / 1_024) };
            var artifact = RevenantSsaInference.FitForecast(ml, spec, trainingBank.ReferenceMinor!.Value, trainingLevels, 0);
            var cases = unseen.Banks.Select(bank =>
            {
                var bankSpec = a.Spec with { ScaleDivisor = Math.Max(1, bank.ReferenceMinor!.Value / 1_024) };
                var levels = bank.Points.Select(p => p.Value).ToArray();
                var steps = RevenantSsaInference.ForecastBank(ml, artifact, bankSpec, null, bank.ReferenceMinor!.Value, levels);
                return R7Evaluation.BankCase(unseen.Manifest.Root.Key, bank, steps, unseen.Events, null);
            }).ToArray();
            var horizons = R7ForecastMetrics.Evaluate(cases, new long[8]);
            return new { a.Name, a.Factor, Spec = a.Spec, ArtifactSha256 = ObservationEncoding.Hash(artifact), Summary = Summaries(horizons),
                Warnings = R7ForecastMetrics.Warnings(cases) };
        }).ToArray();

        Write(output, "pilot-native-diagnostics.json", new
        {
            Source = "TestResults/r7/pilot-43363691834 (generator 3)", TrainingAccount = trainingAccount.SubjectId,
            TrainingBank = trainingBank.SubjectId, PValue = pvalue, StateMemory = memory, Forecast = forecasts,
        });
    }

    private static double[] Residuals(MLContext ml, byte[] artifact, SsaAnomalySpec spec, long[] counts) =>
        RevenantSsaInference.ScoreAccount(ml, artifact, spec with { Warmup = SsaAnomalySpec.StateMemory(spec.SeasonalWindow), MinimumNonzero = 0 },
            new SsaAlertPolicy(SsaAlertStatistic.Residual, 1e9, 0), counts) is var steps && steps.Length > 0
            ? [.. Enumerable.Repeat(double.NaN, steps[0].Bucket), .. steps.Select(s => s.Residual)] : [];

    /// <summary>AUC of the maximum residual inside each labelled anomaly window against all residuals of unlabelled buckets.</summary>
    private static double? ResidualAuc(SsaAnomalyStep[] steps, LongitudinalEvent[] events)
    {
        var anomalies = events.Where(e => e.Kind != LongitudinalEventKind.LegitimateBurst).ToArray();
        var hit = steps.Where(s => anomalies.Any(e => s.Bucket >= e.Start && s.Bucket <= e.End)).GroupBy(s => anomalies
            .First(e => s.Bucket >= e.Start && s.Bucket <= e.End).Id).Select(g => g.Max(s => s.Residual)).ToArray();
        var normal = steps.Where(s => !events.Any(e => s.Bucket >= e.Start && s.Bucket <= e.End + 3)).Select(s => s.Residual).ToArray();
        return R7Diagnostics.Auc(hit, normal);
    }

    internal static R7ForecastMetrics.BankCase[] ArCases(LongitudinalSample root, double[] coefficients) => root.Banks.Select(bank =>
    {
        var opening = bank.ReferenceMinor!.Value;
        var scale = SsaAdvisory.Scale(opening, 1_000);
        var levels = bank.Points.Select(p => p.Value).ToArray();
        var deltas = R7Diagnostics.Changes(opening, levels, scale);
        var origins = Enumerable.Range(288, levels.Length - 288).Select(t =>
        {
            var changes = R7Diagnostics.PredictChanges(coefficients, deltas, t, 8);
            double sum = 0;
            var path = changes.Select(c =>
            {
                sum += c;
                var central = levels[t] + (long)Math.Round(sum * scale);
                return new R7ForecastMetrics.ForecastPoint(central, central, central);
            }).ToArray();
            return new R7ForecastMetrics.BankOrigin(t, levels[t], path, R7Evaluation.TrailingMedian(levels, t));
        }).ToArray();
        return new R7ForecastMetrics.BankCase(root.Manifest.Root.Key, bank.SubjectId, opening, levels, origins,
            root.Events.Where(e => e.Subject == bank.SubjectId).ToArray());
    }).ToArray();

    internal static object[] Summaries(R7ForecastMetrics.HorizonMetrics[] horizons) => horizons.Select(h => (object)new
    {
        h.Horizon, h.Samples, h.Active, h.Stress, h.Mae, h.PersistenceMae, h.ActiveMae, h.ActivePersistenceMae,
        h.StressMae, h.StressPersistenceMae, h.Bias, h.NativeCoverage, h.NativeWidth, h.StressNativeCoverage,
        ActiveSkill = h.ActiveMae is { } a && h.ActivePersistenceMae is > 0 ? 1 - a / h.ActivePersistenceMae : (double?)null,
    }).ToArray();

    private static LongitudinalSample[] Pilot(string input)
    {
        var manifest = LongitudinalSample.ReadManifest(input, "pilot.json");
        return manifest.Roots.Select(r => LongitudinalSample.Read(input, r)).ToArray();
    }

    private static (string Input, string Output) Paths()
    {
        var input = Environment.GetEnvironmentVariable("REVENANT_R7_PILOT_INPUT")
            ?? throw new InvalidOperationException("Set REVENANT_R7_PILOT_INPUT to the retained pilot directory.");
        var output = Environment.GetEnvironmentVariable("REVENANT_R7_DIAGNOSTICS_OUTPUT")
            ?? throw new InvalidOperationException("Set REVENANT_R7_DIAGNOSTICS_OUTPUT.");
        Directory.CreateDirectory(output);
        return (input, output);
    }

    internal static void Write<T>(string folder, string name, T value) =>
        File.WriteAllBytes(Path.Combine(folder, name), JsonSerializer.SerializeToUtf8Bytes(value, Json));
}
