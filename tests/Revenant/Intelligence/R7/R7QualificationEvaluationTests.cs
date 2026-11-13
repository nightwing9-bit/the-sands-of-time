using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.ML;
using Revenant.Observations;
using Revenant.Synthetic;
using Xunit;

namespace Revenant.Tests.Intelligence.R7;

public sealed class R7QualificationEvaluationTests
{
    private const string Policy = "revenant-r7-scientific-evaluation/1";
    private static readonly double[] Thresholds = [.0005, .001, .0025, .005, .01, .02, .035, .05];
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, IncludeFields = true, Converters = { new JsonStringEnumConverter() },
    };

    private sealed record Freeze(string Policy, string Revision, string CohortRun, string AnomalyZipSha256, string ForecastZipSha256,
        string AnomalyTrainingDigest, string ForecastTrainingDigest, R7RollingEvaluation.AnomalyConfig Anomaly,
        R7RollingEvaluation.ForecastConfig Forecast, double SsaThreshold, double BaselineThreshold, long[] IntervalAdjustmentMinor,
        string[] DevelopmentManifestHashes, int Warmup, int MinimumNonzero, long Scale, int Cooldown, int MatchingExtension,
        long ActiveChangeMinor, string Floor, string IntervalMethod);
    private sealed record DevelopmentCandidate(int Window, string AnomalyPath, string ForecastPath,
        R7RollingEvaluation.AnomalyConfig Anomaly, R7RollingEvaluation.ForecastConfig Forecast,
        R7RollingEvaluation.AnomalyMetrics AnomalyValidation, R7ForecastMetrics.HorizonMetrics[] ForecastValidation);
    private sealed record ThresholdResult(double Threshold, R7RollingEvaluation.AnomalyMetrics Metrics);
    private sealed record AccountCase(V4AdvisorySeries Series, R7RollingEvaluation.BucketScore[] Scores, LongitudinalEvent[] Labels);

    [Fact]
    [Trait("Category", "R7Longitudinal")]
    public void Select_on_validation_and_freeze_calibration_before_final_data()
    {
        RequireNative();
        var data = Required("REVENANT_R7_COHORT_INPUT");
        var output = Required("REVENANT_R7_MODEL_OUTPUT");
        Directory.CreateDirectory(output);
        var train = ReadSplit(data, LongitudinalSplit.Train);
        var validation = ReadSplit(data, LongitudinalSplit.Validation);
        var calibration = ReadSplit(data, LongitudinalSplit.Calibration);
        VerifyIndependent([.. train, .. validation, .. calibration]);

        var trainAccounts = train.SelectMany(r => r.Accounts).Where(R7RollingEvaluation.Eligible).ToArray();
        var trainBanks = train.SelectMany(r => r.Banks).ToArray();
        var account = Representative(trainAccounts, s => s.Points.Take(R7RollingEvaluation.Warmup).Count(p => p.Value > 0));
        var bank = Representative(trainBanks, s => s.Points.Take(R7RollingEvaluation.Warmup).Select(p => p.Value).Distinct().Count());
        var ml = new MLContext(seed: 7);
        var candidates = new List<DevelopmentCandidate>();
        foreach (var window in new[] { 8, 12 })
        {
            var anomaly = new R7RollingEvaluation.AnomalyConfig(window, window * 2, 288);
            var forecast = new R7RollingEvaluation.ForecastConfig(window, window * 4, 288);
            var anomalyPath = Path.Combine(output, $"candidate-anomaly-{window}.zip");
            var forecastPath = Path.Combine(output, $"candidate-forecast-{window}.zip");
            R7RollingEvaluation.FitAnomaly(ml, account, anomaly, anomalyPath);
            R7RollingEvaluation.FitForecast(ml, bank, forecast, forecastPath);
            var anomalies = Evaluate(ScoreAccounts(ml, validation, anomalyPath), .005, baseline: false);
            var forecasts = R7ForecastMetrics.Evaluate(ScoreBanks(ml, validation, forecastPath), new long[8]);
            candidates.Add(new(window, anomalyPath, forecastPath, anomaly, forecast, anomalies, forecasts));
        }

        var selectedAnomaly = candidates.OrderByDescending(c => c.AnomalyValidation.Detected)
            .ThenBy(c => c.AnomalyValidation.FalseAlerts).ThenBy(c => c.Window).First();
        var selectedForecast = candidates.OrderBy(c => c.ForecastValidation.Average(h => h.ActiveMae ?? double.MaxValue))
            .ThenBy(c => c.ForecastValidation.Average(h => h.Mae ?? double.MaxValue)).ThenBy(c => c.Window).First();
        var calibratedAccounts = ScoreAccounts(ml, calibration, selectedAnomaly.AnomalyPath);
        var ssa = Thresholds.Prepend(0).Select(p => new ThresholdResult(p, Evaluate(calibratedAccounts, p, baseline: false))).ToArray();
        var baseline = Thresholds.Prepend(0).Select(p => new ThresholdResult(p, Evaluate(calibratedAccounts, p, baseline: true))).ToArray();
        var calibratedBanks = ScoreBanks(ml, calibration, selectedForecast.ForecastPath);
        var adjustment = R7ForecastMetrics.Calibrate(calibratedBanks);
        var anomalyPathOut = Path.Combine(output, "frozen-anomaly.zip");
        var forecastPathOut = Path.Combine(output, "frozen-forecast.zip");
        File.Copy(selectedAnomaly.AnomalyPath, anomalyPathOut);
        File.Copy(selectedForecast.ForecastPath, forecastPathOut);
        var pin = new Freeze(Policy, Required("REVENANT_R7_SOURCE_SHA"), Required("REVENANT_R7_COHORT_RUN"),
            Hash(anomalyPathOut), Hash(forecastPathOut), account.Digest, bank.Digest, selectedAnomaly.Anomaly, selectedForecast.Forecast,
            BestThreshold(ssa).Threshold, BestThreshold(baseline).Threshold, adjustment, ManifestHashes(data,
                [LongitudinalSplit.Train, LongitudinalSplit.Validation, LongitudinalSplit.Calibration]),
            R7RollingEvaluation.Warmup, R7RollingEvaluation.HistoryNonzero, R7RollingEvaluation.Scale, R7RollingEvaluation.Cooldown,
            R7RollingEvaluation.LateMatch, R7RollingEvaluation.ActiveChangeMinor, "synthetic-available-buffer-floor/1:10%",
            "horizon-native-band-nonnegative-95th-miss/1");
        Write(output, "frozen.json", pin);
        Write(output, "development.json", new
        {
            Candidates = candidates, TrainingAccount = account.SubjectId, TrainingBank = bank.SubjectId,
            Calibration = new { Ssa = ssa, Baseline = baseline, IntervalAdjustmentMinor = adjustment },
            CalibrationHorizonMetrics = R7ForecastMetrics.Evaluate(calibratedBanks, adjustment),
            CalibrationWarnings = R7ForecastMetrics.Warnings(calibratedBanks),
            References = new
            {
                Account = new { Digest = account.Digest, Output = R7RollingEvaluation.ScoreAccount(ml, account, anomalyPathOut)[0] },
                Bank = new { Digest = bank.Digest, Output = R7RollingEvaluation.ForecastBank(ml, bank, forecastPathOut)[0] },
            },
        });
    }

    [Fact]
    [Trait("Category", "R7Longitudinal")]
    public void Evaluate_the_frozen_SSA_models_on_final_and_shifted_histories()
    {
        RequireNative();
        var data = Required("REVENANT_R7_COHORT_INPUT");
        var frozen = Required("REVENANT_R7_FROZEN_INPUT");
        var output = Required("REVENANT_R7_MODEL_OUTPUT");
        Directory.CreateDirectory(output);
        var pinPath = Path.Combine(frozen, "frozen.json");
        Assert.Equal(Required("REVENANT_R7_FROZEN_SHA256"), Hash(pinPath));
        var pin = JsonSerializer.Deserialize<Freeze>(File.ReadAllBytes(pinPath), Json)!;
        Assert.Equal(Policy, pin.Policy);
        Assert.Equal(Required("REVENANT_R7_SOURCE_SHA"), pin.Revision);
        Assert.Equal(Required("REVENANT_R7_COHORT_RUN"), pin.CohortRun);
        Assert.Equal(R7RollingEvaluation.Warmup, pin.Warmup);
        Assert.Equal(R7RollingEvaluation.HistoryNonzero, pin.MinimumNonzero);
        Assert.Equal(R7RollingEvaluation.Scale, pin.Scale);
        Assert.Equal(R7RollingEvaluation.Cooldown, pin.Cooldown);
        Assert.Equal(R7RollingEvaluation.LateMatch, pin.MatchingExtension);
        Assert.Equal(R7RollingEvaluation.ActiveChangeMinor, pin.ActiveChangeMinor);
        Assert.Equal(8, pin.IntervalAdjustmentMinor.Length);
        Assert.All(pin.IntervalAdjustmentMinor, n => Assert.True(n >= 0));
        var anomalyPath = Path.Combine(frozen, "frozen-anomaly.zip");
        var forecastPath = Path.Combine(frozen, "frozen-forecast.zip");
        Assert.Equal(pin.AnomalyZipSha256, Hash(anomalyPath));
        Assert.Equal(pin.ForecastZipSha256, Hash(forecastPath));
        var final = ReadSplit(data, LongitudinalSplit.FinalTest);
        var challenge = ReadSplit(data, LongitudinalSplit.Challenge);
        VerifyIndependent([.. final, .. challenge]);
        var ml = new MLContext(seed: 7);
        var partitions = new[] { final, challenge }.Select(roots =>
        {
            var accounts = ScoreAccounts(ml, roots, anomalyPath);
            var banks = ScoreBanks(ml, roots, forecastPath);
            var anomaly = Evaluate(accounts, pin.SsaThreshold, baseline: false);
            var comparator = Evaluate(accounts, pin.BaselineThreshold, baseline: true);
            var horizons = R7ForecastMetrics.Evaluate(banks, pin.IntervalAdjustmentMinor);
            var warnings = R7ForecastMetrics.Warnings(banks);
            var strongestActive = Math.Min(horizons.Average(h => h.ActivePersistenceMae ?? 0), horizons.Average(h => h.ActiveRobustMae ?? 0));
            var strongestOverall = Math.Min(horizons.Average(h => h.PersistenceMae ?? 0), horizons.Average(h => h.RobustMae ?? 0));
            var skill = strongestActive > 0 ? 1 - horizons.Average(h => h.ActiveMae ?? 0) / strongestActive : 0;
            var overallRatio = strongestOverall > 0 ? horizons.Average(h => h.Mae ?? 0) / strongestOverall : double.MaxValue;
            return new
            {
                Split = roots[0].Manifest.Root.Split, Anomaly = anomaly, Comparator = comparator,
                Horizons = horizons, Warnings = warnings, ActiveSkill = skill, OverallErrorRatio = overallRatio,
                PromoteAnomaly = anomaly.FalseAlertsPerNormalDay is <= .1 && anomaly.Detected > comparator.Detected
                    && anomaly.FalseAlertsPerNormalDay <= comparator.FalseAlertsPerNormalDay,
                PromoteForecast = skill >= .05 && overallRatio <= 1.05
                    && horizons.All(h => h.CalibratedCoverage is >= .90)
                    && warnings.DevelopingCrossings > 0 && warnings.WarnedDeveloping * 2 >= warnings.DevelopingCrossings
                    && warnings.Precision is >= .5,
                ByRoot = roots.Select(r =>
                {
                    var subjects = r.Accounts.Select(s => s.SubjectId).ToHashSet(StringComparer.Ordinal);
                    var accountCases = accounts.Where(a => subjects.Contains(a.Series.SubjectId)).ToArray();
                    var bankCases = banks.Where(b => b.Root == r.Manifest.Root.Key).ToArray();
                    return new
                    {
                        r.Manifest.Root, Anomaly = Evaluate(accountCases, pin.SsaThreshold, false),
                        Comparator = Evaluate(accountCases, pin.BaselineThreshold, true),
                        Warnings = R7ForecastMetrics.Warnings(bankCases),
                        Banks = bankCases.Select(b => new
                        {
                            b.Bank, Horizons = R7ForecastMetrics.Evaluate([b], pin.IntervalAdjustmentMinor),
                        }).ToArray(),
                    };
                }).ToArray(),
            };
        }).ToArray();
        Write(output, "final-evaluation.json", new
        {
            FreezeSha256 = Hash(pinPath), pin, Partitions = partitions,
            FinalManifestHashes = ManifestHashes(data, [LongitudinalSplit.FinalTest, LongitudinalSplit.Challenge]),
            PromoteAnomaly = partitions.All(p => p.PromoteAnomaly), PromoteForecast = partitions.All(p => p.PromoteForecast),
        });
    }

    private static V4AdvisorySeries Representative(V4AdvisorySeries[] series, Func<V4AdvisorySeries, int> activity)
    {
        var sorted = series.OrderBy(activity).ThenBy(s => s.ExecutionRunId.Value).ThenBy(s => s.SubjectId, StringComparer.Ordinal).ToArray();
        return sorted[sorted.Length / 2];
    }

    private static ThresholdResult BestThreshold(ThresholdResult[] choices) => choices.Where(t => t.Metrics.FalseAlertsPerNormalDay <= .1)
        .OrderByDescending(t => t.Metrics.Detected).ThenBy(t => t.Metrics.FalseAlerts).ThenBy(t => t.Threshold).First();

    private static AccountCase[] ScoreAccounts(MLContext ml, LongitudinalSample[] roots, string model) => roots
        .SelectMany(r => r.Accounts.Select(s => new AccountCase(s, R7RollingEvaluation.ScoreAccount(ml, s, model),
            r.Events.Where(e => e.Subject == s.SubjectId).ToArray()))).ToArray();

    private static R7ForecastMetrics.BankCase[] ScoreBanks(MLContext ml, LongitudinalSample[] roots, string model) => roots
        .SelectMany(r => r.Banks.Select(s => new R7ForecastMetrics.BankCase(r.Manifest.Root.Key, s.SubjectId,
            s.ReferenceMinor!.Value, s.Points.Select(p => p.Value).ToArray(), R7RollingEvaluation.ForecastBank(ml, s, model),
            r.Events.Where(e => e.Subject == s.SubjectId).ToArray(), r.Targets[s.SubjectId]))).ToArray();

    private static R7RollingEvaluation.AnomalyMetrics Evaluate(AccountCase[] cases, double threshold, bool baseline) =>
        R7RollingEvaluation.EvaluateAnomalies(cases.Select(c => (c.Series, c.Scores, c.Labels)), threshold, baseline);

    private static LongitudinalSample[] ReadSplit(string path, LongitudinalSplit split) =>
        V4LongitudinalPlanner.Roots(split).Select(expected =>
        {
            var folder = Path.Combine(path, $"r7-cohort-{split}-{expected.Index}");
            var manifest = LongitudinalSample.ReadManifest(folder, "qualification.json");
            var actual = Assert.Single(manifest.Roots);
            Assert.Equal(expected, actual.Root);
            Assert.True(actual.Accepted > 0 && actual.Held > 0 && actual.Releases > 0 && actual.ReturnFacts > 0);
            return LongitudinalSample.Read(folder, actual);
        }).ToArray();

    private static string[] ManifestHashes(string path, LongitudinalSplit[] splits) => splits
        .SelectMany(split => V4LongitudinalPlanner.Roots(split).Select(root =>
            Hash(Path.Combine(path, $"r7-cohort-{split}-{root.Index}", "qualification.json")))).ToArray();

    private static void VerifyIndependent(LongitudinalSample[] roots)
    {
        Assert.Equal(roots.Length, roots.Select(r => r.Manifest.Root.Key).Distinct().Count());
        Assert.Equal(roots.Length, roots.Select(r => r.Manifest.Execution).Distinct().Count());
        Assert.Equal(roots.Length, roots.Select(r => r.Manifest.BehaviorSha256).Distinct().Count());
        var accounts = roots.SelectMany(r => r.Accounts).Select(s => s.SubjectId).ToArray();
        Assert.Equal(accounts.Length, accounts.Distinct(StringComparer.Ordinal).Count());
        var events = roots.SelectMany(r => r.Events).Select(e => e.Id).ToArray();
        Assert.Equal(events.Length, events.Distinct(StringComparer.Ordinal).Count());
    }

    private static string Hash(string path) => ObservationEncoding.Hash(File.ReadAllBytes(path));
    private static string Required(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Set {name} for the R7 experiment.");
    private static void Write<T>(string folder, string name, T value) =>
        File.WriteAllBytes(Path.Combine(folder, name), JsonSerializer.SerializeToUtf8Bytes(value, Json));

    private static void RequireNative() => Assert.True(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64);
}
