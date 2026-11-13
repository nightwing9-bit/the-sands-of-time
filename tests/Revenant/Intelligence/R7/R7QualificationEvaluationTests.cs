using System.Collections.Immutable;
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
/// R7/2 qualification on generator-4 PostgreSQL roots. Selection uses Validation, thresholds and interval multipliers use
/// Calibration, and everything is frozen (frozen.json plus two advisory manifests) before FinalTest and Challenge are read.
/// All scores come from <see cref="RevenantSsaInference"/>, the implementation the published Worker executes.
/// </summary>
public sealed class R7QualificationEvaluationTests
{
    internal const string Policy = "revenant-r7-scientific-evaluation/2";

    /// <summary>Thresholds are calibrated to 75% of each budget so sampling variation on independent roots has headroom.</summary>
    internal const double CalibrationMargin = .75;

    internal static readonly double[] SensitivityBudgets = [.05, .1, .2];

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, IncludeFields = true, Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    internal sealed record AnomalyCandidate(string Name, SsaAnomalySpec Spec, SsaAlertStatistic Statistic, double Allowance,
        int Window = 1, double Floor = 0)
    {
        internal SsaAlertPolicy Policy(double threshold = 1e9) => new(Statistic, threshold, Allowance, Window, Floor);
    }
    internal sealed record ForecastCandidate(string Name, SsaForecastSpec Spec);

    /// <summary>
    /// Predeclared anomaly candidates, fixed after the generator-4 development pilot. The first is the primary design: a two-day
    /// seasonal window on raw counts, whose SSA expectation averages the account's last two days, with a 30-minute Poisson
    /// surprise. Each other candidate changes one factor. All share the 1,152-bucket warm-up so they score identical buckets.
    /// </summary>
    internal static readonly AnomalyCandidate[] AnomalyCandidates =
    [
        new("twoday-count-poisson-w6", new(SsaCountTransform.Count, 576, 1_160, 32, 1_152, 8), SsaAlertStatistic.PoissonSurprise, 0, 6, .02),
        new("twoday-count-poisson-w3", new(SsaCountTransform.Count, 576, 1_160, 32, 1_152, 8), SsaAlertStatistic.PoissonSurprise, 0, 3, .02),
        new("twoday-count-poisson-w1", new(SsaCountTransform.Count, 576, 1_160, 32, 1_152, 8), SsaAlertStatistic.PoissonSurprise, 0, 1, .02),
        new("twoday-count-poisson-w3-floor05", new(SsaCountTransform.Count, 576, 1_160, 32, 1_152, 8), SsaAlertStatistic.PoissonSurprise, 0, 3, .05),
        new("daily-count-poisson-w3", new(SsaCountTransform.Count, 288, 1_160, 32, 1_152, 8), SsaAlertStatistic.PoissonSurprise, 0, 3, .02),
        new("twoday-anscombe-residual", new(SsaCountTransform.AnscombeZeroAnchored, 576, 1_160, 32, 1_152, 8), SsaAlertStatistic.Residual, 0),
    ];

    /// <summary>Predeclared forecast candidates. Window 24 covers two periods of the longest generator-4 treasury cycle.</summary>
    internal static readonly ForecastCandidate[] ForecastCandidates =
    [
        new("change-w24", new(SsaForecastRepresentation.CompletedIntervalChange, 24, 96, 2016, null, 288, 1_000)),
        new("change-w12", new(SsaForecastRepresentation.CompletedIntervalChange, 12, 48, 2016, null, 288, 1_000)),
        new("level-w24", new(SsaForecastRepresentation.OpeningCenteredLevel, 24, 96, 2016, null, 288, 1_000)),
    ];

    internal const int SigmaWindow = 12;
    internal const double IntervalCoverage = .95;

    internal sealed record Freeze(string Policy, string Revision, string CohortRun, string Generator, string AnomalyCandidate,
        string ForecastCandidate, SsaAdvisoryManifest Anomaly, SsaAdvisoryManifest Forecast, string AnomalyManifestSha256,
        string ForecastManifestSha256, Dictionary<string, double> SsaThresholds, Dictionary<string, double> BaselineThresholds,
        string[] DevelopmentManifestHashes, double Budget, double CalibrationMargin, int Cooldown, int MatchingExtension,
        long ActiveChangeMinor, string Floor);

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
        var ml = new MLContext(seed: 7);
        var development = Develop(ml, train, validation, calibration);

        var anomalyManifest = development.AnomalyManifest;
        var forecastManifest = development.ForecastManifest;
        var anomalyDir = Path.Combine(output, "revenant-ssa", "anomaly");
        var forecastDir = Path.Combine(output, "revenant-ssa", "forecast");
        var anomalySha = WriteArtifact(anomalyDir, "anomaly", development.AnomalyArtifact, anomalyManifest);
        var forecastSha = WriteArtifact(forecastDir, "forecast", development.ForecastArtifact, forecastManifest);
        var pin = new Freeze(Policy, Required("REVENANT_R7_SOURCE_SHA"), Required("REVENANT_R7_COHORT_RUN"),
            V4LongitudinalPlannerG4.Version, development.AnomalyCandidate, development.ForecastCandidate, anomalyManifest, forecastManifest,
            anomalySha, forecastSha, development.SsaThresholds, development.BaselineThresholds,
            ManifestHashes(data, [LongitudinalSplit.Train, LongitudinalSplit.Validation, LongitudinalSplit.Calibration]),
            R7Evaluation.FalseAlertBudget, CalibrationMargin, R7Evaluation.Cooldown, R7Evaluation.LateMatch,
            R7ForecastMetrics.ActiveChangeMinor, "synthetic-available-buffer-floor/1:10%");
        Write(output, "frozen.json", pin);
        Write(output, "development.json", development.Report);
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
        Assert.Equal(V4LongitudinalPlannerG4.Version, pin.Generator);
        Assert.Equal((R7Evaluation.FalseAlertBudget, CalibrationMargin, R7Evaluation.Cooldown, R7Evaluation.LateMatch,
            R7ForecastMetrics.ActiveChangeMinor), (pin.Budget, pin.CalibrationMargin, pin.Cooldown, pin.MatchingExtension, pin.ActiveChangeMinor));
        var anomaly = ReadArtifact(Path.Combine(frozen, "revenant-ssa", "anomaly"), "anomaly", pin.AnomalyManifestSha256, pin.Anomaly);
        var forecast = ReadArtifact(Path.Combine(frozen, "revenant-ssa", "forecast"), "forecast", pin.ForecastManifestSha256, pin.Forecast);
        var final = ReadSplit(data, LongitudinalSplit.FinalTest);
        var challenge = ReadSplit(data, LongitudinalSplit.Challenge);
        VerifyIndependent([.. final, .. challenge]);
        var ml = new MLContext(seed: 7);
        var partitions = new[] { final, challenge }.Select(roots =>
            Judge(ml, roots, anomaly, pin.Anomaly, pin.SsaThresholds, pin.BaselineThresholds, forecast, pin.Forecast)).ToArray();
        Write(output, "final-evaluation.json", new
        {
            FreezeSha256 = Hash(pinPath), pin, Partitions = partitions,
            FinalManifestHashes = ManifestHashes(data, [LongitudinalSplit.FinalTest, LongitudinalSplit.Challenge]),
            PromoteAnomaly = partitions.All(p => p.PromoteAnomaly), PromoteForecast = partitions.All(p => p.PromoteForecast),
        });
    }

    internal sealed record AnomalyDevelopment(string Candidate, byte[] Artifact, SsaAdvisoryManifest Manifest,
        Dictionary<string, double> SsaThresholds, Dictionary<string, double> BaselineThresholds, object Report);

    internal sealed record ForecastDevelopment(string Candidate, byte[] Artifact, SsaAdvisoryManifest Manifest, object Report);

    internal sealed record Development(string AnomalyCandidate, string ForecastCandidate, byte[] AnomalyArtifact, byte[] ForecastArtifact,
        SsaAdvisoryManifest AnomalyManifest, SsaAdvisoryManifest ForecastManifest, Dictionary<string, double> SsaThresholds,
        Dictionary<string, double> BaselineThresholds, object Report);

    internal static Development Develop(MLContext ml, LongitudinalSample[] train, LongitudinalSample[] validation,
        LongitudinalSample[] calibration)
    {
        var anomaly = DevelopAnomaly(ml, R7Evaluation.Inputs(train), R7Evaluation.Inputs(validation), R7Evaluation.Inputs(calibration),
            V4LongitudinalPlannerG4.Version);
        var forecast = DevelopForecast(ml, train, validation, calibration, V4LongitudinalPlannerG4.Version);
        return new(anomaly.Candidate, forecast.Candidate, anomaly.Artifact, forecast.Artifact, anomaly.Manifest, forecast.Manifest,
            anomaly.SsaThresholds, anomaly.BaselineThresholds, new { Anomaly = anomaly.Report, Forecast = forecast.Report });
    }

    /// <summary>
    /// Fit every anomaly candidate on one training account, select on validation accounts by recall at the margin-adjusted budget,
    /// then calibrate SSA and comparator thresholds for each sensitivity budget on calibration accounts.
    /// </summary>
    internal static AnomalyDevelopment DevelopAnomaly(MLContext ml, R7Evaluation.AccountInput[] train, R7Evaluation.AccountInput[] validation,
        R7Evaluation.AccountInput[] calibration, string generator)
    {
        var trainingAccount = TrainingAccount(train, AnomalyCandidates[0].Spec);
        var artifacts = new Dictionary<SsaAnomalySpec, byte[]>();
        var anomalyResults = AnomalyCandidates.Select(candidate =>
        {
            if (!artifacts.TryGetValue(candidate.Spec, out var artifact))
                artifacts[candidate.Spec] = artifact = RevenantSsaInference.FitAnomaly(ml, candidate.Spec, trainingAccount.Counts, 0);
            var policy = candidate.Policy();
            var cases = Accounts(ml, validation, artifact, candidate.Spec, policy);
            var (threshold, metrics) = R7Evaluation.Calibrate(cases, R7Evaluation.Scorer.Ssa, policy,
                R7Evaluation.FalseAlertBudget * CalibrationMargin);
            var baseline = R7Evaluation.Calibrate(cases, R7Evaluation.Scorer.TrailingRank, null, R7Evaluation.FalseAlertBudget * CalibrationMargin);
            return (Candidate: candidate, Artifact: artifact, Threshold: threshold, Metrics: metrics, Baseline: baseline);
        }).ToArray();
        var selected = anomalyResults.OrderByDescending(r => r.Metrics.Detected).ThenBy(r => r.Metrics.FalseAlerts)
            .ThenBy(r => Array.IndexOf(AnomalyCandidates, r.Candidate)).First();
        var policy = selected.Candidate.Policy();
        var calibrationAccounts = Accounts(ml, calibration, selected.Artifact, selected.Candidate.Spec, policy);
        var ssaThresholds = SensitivityBudgets.ToDictionary(Key, b => R7Evaluation.Calibrate(calibrationAccounts,
            R7Evaluation.Scorer.Ssa, policy, b * CalibrationMargin).Threshold);
        var baselineThresholds = SensitivityBudgets.ToDictionary(Key, b => R7Evaluation.Calibrate(calibrationAccounts,
            R7Evaluation.Scorer.TrailingRank, null, b * CalibrationMargin).Threshold);
        var manifest = new SsaAdvisoryManifest(SsaAdvisory.Policy, "anomaly", ObservationEncoding.Hash(selected.Artifact), generator,
            trainingAccount.Digest, selected.Candidate.Spec, policy with { Threshold = ssaThresholds[Key(R7Evaluation.FalseAlertBudget)] },
            null, null, SsaAdvisoryManifest.CausalAnomalyExpectation);
        Assert.True(manifest.IsValidFor("anomaly"));
        return new(selected.Candidate.Name, selected.Artifact, manifest, ssaThresholds, baselineThresholds, new
        {
            TrainingAccount = trainingAccount.Subject, TrainingAccountDigest = trainingAccount.Digest,
            Validation = anomalyResults.Select(r => new
            {
                r.Candidate, ArtifactSha256 = ObservationEncoding.Hash(r.Artifact), r.Threshold, r.Metrics,
                BaselineThreshold = r.Baseline.Threshold, BaselineMetrics = r.Baseline.Metrics,
            }),
            Selected = selected.Candidate.Name, SsaThresholds = ssaThresholds, BaselineThresholds = baselineThresholds,
        });
    }

    /// <summary>
    /// Fit every forecast candidate on one training bank history, select on validation banks (overall ratio gate first, then
    /// active skill), and calibrate locally scaled split-conformal multipliers on calibration banks.
    /// </summary>
    internal static ForecastDevelopment DevelopForecast(MLContext ml, LongitudinalSample[] train, LongitudinalSample[] validation,
        LongitudinalSample[] calibration, string generator)
    {
        var trainingBank = TrainingBank(train);
        var levels = trainingBank.Points.Select(p => p.Value).ToArray();
        var results = ForecastCandidates.Select(candidate =>
        {
            var spec = candidate.Spec with { TrainSize = Math.Min(candidate.Spec.TrainSize, levels.Length) };
            var artifact = RevenantSsaInference.FitForecast(ml, spec, trainingBank.ReferenceMinor!.Value, levels, 0);
            var cases = Banks(ml, validation, artifact, spec, null).Select(b => b.Case).ToArray();
            var horizons = R7ForecastMetrics.Evaluate(cases);
            return (Candidate: candidate with { Spec = spec }, Artifact: artifact, Horizons: horizons, Skill: Skill(horizons),
                Ratio: OverallRatio(horizons), Warnings: R7ForecastMetrics.Warnings(cases));
        }).ToArray();
        var selected = results.OrderByDescending(r => r.Ratio <= 1.05).ThenByDescending(r => r.Skill)
            .ThenBy(r => Array.FindIndex(ForecastCandidates, c => c.Name == r.Candidate.Name)).First();
        var placeholder = new SsaIntervalPolicy(SigmaWindow, IntervalCoverage, [.. new double[SsaAdvisory.Horizon]]);
        var calibrationBanks = Banks(ml, calibration, selected.Artifact, selected.Candidate.Spec, placeholder);
        var multipliers = R7Evaluation.ConformalMultipliers(calibrationBanks.Select(b => (b.Steps, (Func<int, int, long?>)b.Case.Target)),
            IntervalCoverage);
        var interval = placeholder with { Multipliers = [.. multipliers] };
        var manifest = new SsaAdvisoryManifest(SsaAdvisory.Policy, "forecast", ObservationEncoding.Hash(selected.Artifact), generator,
            trainingBank.Digest, null, null, selected.Candidate.Spec, interval);
        Assert.True(manifest.IsValidFor("forecast"));
        return new(selected.Candidate.Name, selected.Artifact, manifest, new
        {
            TrainingBank = trainingBank.SubjectId, TrainingBankDigest = trainingBank.Digest,
            Validation = results.Select(r => new
            {
                r.Candidate, ArtifactSha256 = ObservationEncoding.Hash(r.Artifact), r.Skill, r.Ratio, r.Warnings,
                Horizons = R7RetainedPilotDiagnosticsTests.Summaries(r.Horizons),
            }),
            Selected = selected.Candidate.Name, Interval = interval,
            CalibrationHorizons = R7RetainedPilotDiagnosticsTests.Summaries(R7ForecastMetrics.Evaluate(Banks(ml, calibration,
                selected.Artifact, selected.Candidate.Spec, interval).Select(b => b.Case).ToArray())),
        });
    }

    internal sealed record Judgement(LongitudinalSplit Split, R7Evaluation.AnomalyMetrics Anomaly, R7Evaluation.AnomalyMetrics Comparator,
        Dictionary<string, object> Sensitivity, R7ForecastMetrics.HorizonMetrics[] Horizons, R7ForecastMetrics.WarningMetrics Warnings,
        double ActiveSkill, double OverallErrorRatio, bool PromoteAnomaly, bool PromoteForecast, object ByRoot);

    internal static Judgement Judge(MLContext ml, LongitudinalSample[] roots, byte[] anomalyArtifact, SsaAdvisoryManifest anomaly,
        Dictionary<string, double> ssaThresholds, Dictionary<string, double> baselineThresholds, byte[] forecastArtifact,
        SsaAdvisoryManifest forecast)
    {
        var accounts = Accounts(ml, R7Evaluation.Inputs(roots), anomalyArtifact, anomaly.Anomaly!, anomaly.Alert!);
        var banks = Banks(ml, roots, forecastArtifact, forecast.Forecast!, forecast.Interval!).Select(b => b.Case).ToArray();
        var key = Key(R7Evaluation.FalseAlertBudget);
        var ssa = R7Evaluation.Evaluate(accounts, R7Evaluation.Scorer.Ssa, anomaly.Alert, ssaThresholds[key]);
        var comparator = R7Evaluation.Evaluate(accounts, R7Evaluation.Scorer.TrailingRank, null, baselineThresholds[key]);
        var horizons = R7ForecastMetrics.Evaluate(banks);
        var warnings = R7ForecastMetrics.Warnings(banks);
        var skill = Skill(horizons);
        var ratio = OverallRatio(horizons);
        return new(roots[0].Manifest.Root.Split, ssa, comparator,
            SensitivityBudgets.ToDictionary(Key, b => (object)new
            {
                Ssa = R7Evaluation.Evaluate(accounts, R7Evaluation.Scorer.Ssa, anomaly.Alert, ssaThresholds[Key(b)]),
                Comparator = R7Evaluation.Evaluate(accounts, R7Evaluation.Scorer.TrailingRank, null, baselineThresholds[Key(b)]),
            }),
            horizons, warnings, skill, ratio,
            // A comparator that never alerts has zero false alerts, so "SSA burden <= comparator burden" penalizes any useful
            // detector. Compare at equal realized burden instead: each comparator gets its best threshold on these same cases with
            // no more false alerts per normal day than the SSA realized. Choosing that threshold with test labels favors the comparator.
            PromoteAnomaly: ssa.FalseAlertsPerNormalDay is <= R7Evaluation.FalseAlertBudget
                && new[] { R7Evaluation.Scorer.TrailingRank, R7Evaluation.Scorer.SeasonalExcess }.All(scorer =>
                    ssa.Detected > R7Evaluation.Calibrate(accounts, scorer, null, ssa.FalseAlertsPerNormalDay ?? 0).Metrics.Detected),
            PromoteForecast: skill >= .05 && ratio <= 1.05 && horizons.All(h => h.CalibratedCoverage is >= .90)
                && warnings.DevelopingCrossings > 0 && warnings.WarnedDeveloping * 2 >= warnings.DevelopingCrossings
                && warnings.Precision is >= .5,
            ByRoot: roots.Select(r => new
            {
                r.Manifest.Root,
                Warnings = R7ForecastMetrics.Warnings(banks.Where(b => b.Root == r.Manifest.Root.Key).ToArray()),
                Banks = banks.Where(b => b.Root == r.Manifest.Root.Key).Select(b => new
                {
                    b.Bank, Horizons = R7RetainedPilotDiagnosticsTests.Summaries(R7ForecastMetrics.Evaluate([b])),
                }).ToArray(),
            }).ToArray());
    }

    /// <summary>Active-slice skill against the stronger of persistence and the trailing-median comparator, averaged over horizons.</summary>
    internal static double Skill(R7ForecastMetrics.HorizonMetrics[] horizons)
    {
        var strongest = Math.Min(horizons.Average(h => h.ActivePersistenceMae ?? 0), horizons.Average(h => h.ActiveRobustMae ?? 0));
        return strongest > 0 ? 1 - horizons.Average(h => h.ActiveMae ?? 0) / strongest : 0;
    }

    internal static double OverallRatio(R7ForecastMetrics.HorizonMetrics[] horizons)
    {
        var strongest = Math.Min(horizons.Average(h => h.PersistenceMae ?? 0), horizons.Average(h => h.RobustMae ?? 0));
        return strongest > 0 ? horizons.Average(h => h.Mae ?? 0) / strongest : double.MaxValue;
    }

    /// <summary>The training account is the eligible training account whose first training window has the strongest daily rhythm.</summary>
    internal static R7Evaluation.AccountInput TrainingAccount(R7Evaluation.AccountInput[] train, SsaAnomalySpec spec) => train
        .Where(s => s.Counts.Length >= spec.TrainingWindow && SsaAdvisory.EligibleAccount(spec, s.Counts))
        .Select(s => (Input: s, Rhythm: R7Diagnostics.Autocorrelation(s.Counts.Take(spec.TrainingWindow).Select(c => (double)c).ToArray(),
            [288])[0] ?? -1))
        .OrderByDescending(x => x.Rhythm).ThenBy(x => x.Input.Root, StringComparer.Ordinal).ThenBy(x => x.Input.Subject, StringComparer.Ordinal)
        .First().Input;

    /// <summary>The training bank is the training-root bank history with the largest completed-interval change variance (in bank scale).</summary>
    internal static V4AdvisorySeries TrainingBank(LongitudinalSample[] train) => train.SelectMany(r => r.Banks)
        .Select(s => (Series: s, Variance: Variance(R7Diagnostics.Changes(s.ReferenceMinor!.Value, s.Points.Select(p => p.Value).ToArray(),
            SsaAdvisory.Scale(s.ReferenceMinor!.Value, 1_000)))))
        .OrderByDescending(x => x.Variance).ThenBy(x => x.Series.ExecutionRunId.Value).ThenBy(x => x.Series.SubjectId, StringComparer.Ordinal)
        .First().Series;

    private static double Variance(double[] x)
    {
        var mean = x.Average();
        return x.Average(v => (v - mean) * (v - mean));
    }

    internal static R7Evaluation.AccountCase[] Accounts(MLContext ml, R7Evaluation.AccountInput[] inputs, byte[] artifact,
        SsaAnomalySpec spec, SsaAlertPolicy policy) => inputs.Select(s => new R7Evaluation.AccountCase(s.Root, s.Subject, s.Rhythm,
            s.Counts, RevenantSsaInference.ScoreAccount(ml, artifact, spec, policy, s.Counts), s.Labels)).ToArray();

    internal static (R7ForecastMetrics.BankCase Case, SsaForecastStep[] Steps)[] Banks(MLContext ml, LongitudinalSample[] roots,
        byte[] artifact, SsaForecastSpec spec, SsaIntervalPolicy? interval) => roots.SelectMany(r => r.Banks.Select(s =>
    {
        var steps = RevenantSsaInference.ForecastBank(ml, artifact, spec, interval, s.ReferenceMinor!.Value,
            s.Points.Select(p => p.Value).ToArray());
        return (R7Evaluation.BankCase(r.Manifest.Root.Key, s, steps, r.Events, r.Targets.GetValueOrDefault(s.SubjectId)), steps);
    })).ToArray();

    internal static string Key(double budget) => budget.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static string WriteArtifact(string root, string kind, byte[] artifact, SsaAdvisoryManifest manifest)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
        var directory = Path.Combine(root, ObservationEncoding.Hash(bytes));
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "manifest.json"), bytes);
        File.WriteAllBytes(Path.Combine(directory, kind + ".zip"), artifact);
        return ObservationEncoding.Hash(bytes);
    }

    private static byte[] ReadArtifact(string root, string kind, string manifestSha, SsaAdvisoryManifest expected)
    {
        var directory = Path.Combine(root, manifestSha);
        var bytes = File.ReadAllBytes(Path.Combine(directory, "manifest.json"));
        Assert.Equal(manifestSha, ObservationEncoding.Hash(bytes));
        var manifest = JsonSerializer.Deserialize<SsaAdvisoryManifest>(bytes, Json)!;
        Assert.True(manifest.IsValidFor(kind));
        Assert.Equal(JsonSerializer.Serialize(expected, Json), JsonSerializer.Serialize(manifest, Json));
        var artifact = File.ReadAllBytes(Path.Combine(directory, kind + ".zip"));
        Assert.Equal(manifest.ArtifactSha256, ObservationEncoding.Hash(artifact));
        return artifact;
    }

    internal static LongitudinalSample[] ReadSplit(string path, LongitudinalSplit split) =>
        V4LongitudinalPlannerG4.Roots(split).Select(expected =>
        {
            var folder = Path.Combine(path, $"r7-cohort-{split}-{expected.Index}");
            var manifest = LongitudinalSample.ReadManifest(folder, "qualification.json");
            Assert.Equal(V4LongitudinalPlannerG4.Version, manifest.Generator);
            var actual = Assert.Single(manifest.Roots);
            Assert.Equal(expected, actual.Root);
            Assert.True(actual.Accepted > 0 && actual.Held > 0 && actual.Releases > 0 && actual.ReturnFacts > 0);
            return LongitudinalSample.Read(folder, actual);
        }).ToArray();

    private static string[] ManifestHashes(string path, LongitudinalSplit[] splits) => splits
        .SelectMany(split => V4LongitudinalPlannerG4.Roots(split).Select(root =>
            Hash(Path.Combine(path, $"r7-cohort-{split}-{root.Index}", "qualification.json")))).ToArray();

    internal static void VerifyIndependent(LongitudinalSample[] roots)
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
    internal static string Required(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Set {name} for the R7 experiment.");
    internal static void Write<T>(string folder, string name, T value) =>
        File.WriteAllBytes(Path.Combine(folder, name), JsonSerializer.SerializeToUtf8Bytes(value, Json));

    internal static void RequireNative() => Assert.True(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64);
}
