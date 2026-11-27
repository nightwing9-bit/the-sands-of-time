using System.Text.Json;
using Hosts.Worker;
using Microsoft.ML;
using Revenant.Intelligence;
using Revenant.Observations;
using Revenant.Synthetic;
using Xunit;

namespace Revenant.Tests.Intelligence.R7;

/// <summary>Explicit offline recovery and calibration on the retained G4 pilot; never run by the runtime.</summary>
public sealed class R7OptionARecoveryTests
{
    private const string TrainingDigest = "00f5fcdb0dcf1f91f46969bfef23f75e7e4f1df4c69a20c58952ee3cec5e43c5";
    private const string PilotDigest = "19b136929aec3e30032995bb7004dbb102693e810ea772007e04073d004e3ce6";
    private const double Allowance = .075;
    private static readonly R7QualificationEvaluationTests.AnomalyCandidate Candidate = R7QualificationEvaluationTests.AnomalyCandidates[0];
    private static readonly JsonSerializerOptions Json = R7QualificationEvaluationTests.Json;
    private static string Output => Required("REVENANT_R7_OPTION_A_OUTPUT");
    private static string Required(string key) => Environment.GetEnvironmentVariable(key) ?? throw new InvalidOperationException($"Set {key}.");
    private static string Zip => Path.Combine(Output, "anomaly.zip");

    private static LongitudinalSample[] Pilot()
    {
        var dir = Required("REVENANT_R7_PILOT_INPUT");
        ObservationEncoding.RequireDigest(File.ReadAllBytes(Path.Combine(dir, "pilot.json")), PilotDigest);
        var manifest = LongitudinalSample.ReadManifest(dir, "pilot.json");
        Assert.Equal(V4LongitudinalPlannerG4.Version, manifest.Generator);
        Assert.Equal(V4LongitudinalPlannerG4.Roots(LongitudinalSplit.Pilot).Take(3), manifest.Roots.Select(r => r.Root));
        return manifest.Roots.Select(r => LongitudinalSample.Read(dir, r)).ToArray();
    }

    private sealed record Trace(string Root, string Subject, string StreamSha256, bool Eligible, SsaAnomalyStep[] Steps);
    private sealed record Scored(string PilotSha256, string ArtifactSha256, string TrainingRoot, string TrainingSubject,
        string TrainingSeriesSha256, SsaAnomalySpec Spec, SsaAlertPolicy ScorePolicy, Trace[] Traces);

    [Fact]
    [Trait("Category", "OfflineTraining")]
    public void Recover_G4_anomaly_once_and_save_verified_causal_traces()
    {
        var pilot = Pilot();
        var training = R7QualificationEvaluationTests.TrainingAccount(R7Evaluation.Inputs([pilot[0]]), Candidate.Spec);
        Assert.Equal("BANK-A/eafc18ee2bb318b6/ACC-0003", training.Subject);
        Assert.Equal(TrainingDigest, training.Digest);
        Console.WriteLine($"Verified original canonical G4 count series: {training.Digest}");
        var ml = new MLContext(seed: 7);
        byte[] fitted;
        // Acquire the output path before fitting so an accidental rerun cannot fit again.
        using (var destination = new FileStream(Zip, FileMode.CreateNew, FileAccess.Write))
        {
            fitted = RevenantSsaInference.FitAnomaly(ml, Candidate.Spec, training.Counts, 0);
            destination.Write(fitted);
            destination.Flush(flushToDisk: true);
        }
        var saved = File.ReadAllBytes(Zip);
        Assert.Equal(fitted, saved);
        var fresh = new MLContext(seed: 7);
        var policy = Candidate.Policy();
        var inputs = R7Evaluation.Inputs(pilot.Skip(1));
        var cases = R7QualificationEvaluationTests.Accounts(fresh, inputs, saved, Candidate.Spec, policy);
        foreach (var c in cases.Where(c => c.Steps.Length > 0).GroupBy(c => c.Root).SelectMany(g => g.Take(2)))
        {
            foreach (var n in new[] { Candidate.Spec.Warmup + 1, Candidate.Spec.TrainingWindow, c.Counts.Length })
            {
                var counts = c.Counts[..n];
                Assert.Equal(c.Steps[n - 1 - Candidate.Spec.Warmup],
                    RevenantSsaInference.ScoreAccount(ml, fitted, Candidate.Spec, policy, counts)[^1]);
                counts[^1] += 5;
                Assert.Equal(c.Steps[n - 1 - Candidate.Spec.Warmup].ExpectedCount,
                    RevenantSsaInference.ScoreAccount(fresh, saved, Candidate.Spec, policy, counts)[^1].ExpectedCount);
            }
            Assert.Empty(RevenantSsaInference.ScoreAccount(fresh, saved, Candidate.Spec, policy, c.Counts[..Candidate.Spec.Warmup]));
            Assert.Empty(RevenantSsaInference.ScoreAccount(fresh, saved, Candidate.Spec, policy, new long[c.Counts.Length]));
        }
        var traces = cases.Zip(inputs, (c, i) => new Trace(c.Root, c.Subject, i.Digest, c.Steps.Length > 0, c.Steps)).ToArray();
        Write("scored.json", new Scored(PilotDigest, ObservationEncoding.Hash(saved), training.Root, training.Subject,
            training.Digest, Candidate.Spec, policy, traces), compact: true);
    }

    [Fact]
    [Trait("Category", "OfflineTraining")]
    public void Calibrate_saved_G4_scores_and_measure_development_policy()
    {
        var pilot = Pilot();
        var scored = JsonSerializer.Deserialize<Scored>(File.ReadAllBytes(Path.Combine(Output, "scored.json")), Json)!;
        Assert.Equal(PilotDigest, scored.PilotSha256);
        Assert.Equal(TrainingDigest, scored.TrainingSeriesSha256);
        var saved = File.ReadAllBytes(Zip);
        Assert.Equal(scored.ArtifactSha256, ObservationEncoding.Hash(saved));
        var policy = Candidate.Policy();
        var roots = pilot.Skip(1).Select(r =>
        {
            var inputs = R7Evaluation.Inputs([r]);
            return inputs.Select(i =>
            {
                var t = Assert.Single(scored.Traces, t => t.Root == i.Root && t.Subject == i.Subject);
                Assert.Equal(i.Digest, t.StreamSha256);
                Assert.Equal(SsaAdvisory.EligibleAccount(Candidate.Spec, i.Counts), t.Eligible);
                return new R7Evaluation.AccountCase(i.Root, i.Subject, i.Rhythm, i.Counts, t.Steps, i.Labels);
            }).ToArray();
        }).ToArray();

        var folds = new[] { (Calibration: 0, Measurement: 1), (Calibration: 1, Measurement: 0) };
        var feasibility = folds.Select(f =>
        {
            var activity = roots[f.Calibration].Where(c => c.Steps.Length > 0).Select(Activity).Order().ToArray();
            var low = NearestRank(activity, 1d / 3);
            var high = NearestRank(activity, 2d / 3);
            return new { f.Calibration, f.Measurement, Low = low, High = high,
                CalibrationExposure = Exposure(roots[f.Calibration], low, high),
                MeasurementExposure = Exposure(roots[f.Measurement], low, high) };
        }).ToArray();
        // Even zero false alerts need ~30 account-days for a one-sided Poisson 95% upper bound of 0.1/day.
        // With fewer days, a separate threshold can only be supported by a very small alert count.
        var canStratify = feasibility.All(f => f.Low < f.High &&
            f.CalibrationExposure.All(s => s.Accounts > 0 && s.Days >= 30 && s.Events > 0) &&
            f.MeasurementExposure.All(s => s.Accounts > 0 && s.Events > 0));
        Write("feasibility.json", new { MinimumCalibrationDays = 30, CanStratify = canStratify, Folds = feasibility });
        Assert.False(canStratify, "Strata support the optional A/B; run it before selecting a policy.");

        var reports = folds.Select(f =>
        {
            var calibration = roots[f.Calibration];
            var measurement = roots[f.Measurement];
            var fit = R7Evaluation.Calibrate(calibration, R7Evaluation.Scorer.Ssa, policy, Allowance);
            var measured = R7Evaluation.Evaluate(measurement, R7Evaluation.Scorer.Ssa, policy, fit.Threshold);
            var seasonal = R7Evaluation.Calibrate(WithSeasonal(calibration), R7Evaluation.Scorer.Ssa, policy, Allowance);
            var seasonalMeasured = R7Evaluation.Evaluate(WithSeasonal(measurement), R7Evaluation.Scorer.Ssa, policy, seasonal.Threshold);
            return new { CalibrationRoot = calibration[0].Root, MeasurementRoot = measurement[0].Root, fit.Threshold,
                Calibration = fit.Metrics, Measurement = measured,
                Seasonal = new { seasonal.Threshold, Calibration = seasonal.Metrics, Measurement = seasonalMeasured } };
        }).ToArray();
        var selected = policy with { Threshold = reports[0].Threshold };
        var manifest = new SsaAdvisoryManifest(SsaAdvisory.Policy, "anomaly", scored.ArtifactSha256, V4LongitudinalPlannerG4.Version,
            scored.TrainingSeriesSha256, Candidate.Spec, selected, null, null, SsaAdvisoryManifest.CausalAnomalyExpectation);
        Write("manifest.json", manifest);
        var modelId = "revenant-ssa-anomaly/" + ObservationEncoding.Hash(File.ReadAllBytes(Path.Combine(Output, "manifest.json")));
        var active = roots.SelectMany(r => r).Select(c =>
        {
            var buckets = c.Steps.Where(s => s.Count > 0 && s.Statistic >= selected.Threshold).Select(s => s.Bucket).ToArray();
            return new { c.Root, c.Subject, AlertBuckets = buckets, Events = R7ForecastMetrics.Coalesce(buckets, R7Evaluation.Cooldown) };
        }).ToArray();
        Write("alerts.json", new { ModelId = modelId, Policy = "R7-AnomalyGlobalPolicy/1", ArtifactSha256 = scored.ArtifactSha256,
            selected.Threshold, Accounts = active }, compact: true);

        var reference = roots[1].Where(c => c.Steps.Length > 0).Take(2).SelectMany(c =>
        {
            var s = pilot[2].Accounts.Single(s => s.SubjectId == c.Subject);
            return new[] { c.Steps[0], c.Steps.FirstOrDefault(x => x.Count > 0), c.Steps[^1] }.Distinct().Select(step => new
            {
                c.Root, c.Subject, StreamSha256 = s.Digest, s.Points[step.Bucket].Seal.BoundaryMs,
                Step = step with { Alert = step.Count > 0 && step.Statistic >= selected.Threshold },
            });
        }).ToArray();
        Write("development.json", new
        {
            ModelId = modelId, scored.ArtifactSha256, scored.PilotSha256, scored.TrainingRoot, scored.TrainingSubject,
            scored.TrainingSeriesSha256, scored.Spec, Trainer = "DetectSpikeBySsa; positive side; confidence 95; seed 7; Single",
            Runtime = "ML.NET 5.0.1; .NET 10; Linux AMD64", SeriesSchema = V4AdvisorySeries.AccountSchema,
            Policy = "R7-AnomalyGlobalPolicy/1", selected.Threshold, Allowance, R7Evaluation.Cooldown, R7Evaluation.LateMatch,
            Status = "SimulationEvaluatedIndependentQualificationPending", SelectedCalibrationRoot = reports[0].CalibrationRoot,
            Folds = reports, Stratification = feasibility, References = reference,
        });
    }

    private static R7Evaluation.AccountCase[] WithSeasonal(R7Evaluation.AccountCase[] cases) => cases.Select(c =>
        c with { Steps = c.Steps.Select(s => s with { ExpectedCount = R7Diagnostics.SeasonalMean(c.Counts, s.Bucket, s.Bucket) }).ToArray() }).ToArray();

    private static long Activity(R7Evaluation.AccountCase c)
    {
        var counts = c.Counts.Take(Candidate.Spec.Warmup).ToArray();
        return NearestRank(Enumerable.Range(5, counts.Length - 5).Select(i => counts.AsSpan(i - 5, 6).ToArray().Sum()).Order().ToArray(), .99);
    }

    private static long NearestRank(long[] sorted, double p) => sorted[(int)Math.Ceiling(p * sorted.Length) - 1];

    private sealed record Stratum(int Accounts, double Days, int Events, double CommercialDays, int CommercialHardNegatives,
        int CommercialNormalActiveBuckets);

    private static Stratum[] Exposure(R7Evaluation.AccountCase[] cases, long low, long high) =>
        Enumerable.Range(0, 3).Select(i =>
        {
            var group = cases.Where(c => c.Steps.Length > 0).Where(c =>
            {
                var a = Activity(c);
                return (a <= low ? 0 : a <= high ? 1 : 2) == i;
            }).ToArray();
            var m = R7Evaluation.Evaluate(group, R7Evaluation.Scorer.Ssa, Candidate.Policy(), double.PositiveInfinity);
            var commercial = group.Where(c => c.Rhythm == "Commercial").ToArray();
            var cm = R7Evaluation.Evaluate(commercial, R7Evaluation.Scorer.Ssa, Candidate.Policy(), double.PositiveInfinity);
            return new Stratum(m.QualifiedAccounts, m.NormalAccountDays, m.Events, cm.NormalAccountDays, cm.HardNegatives,
                commercial.Sum(c => c.Steps.Count(s => s.Count > 0 && !c.Labels.Any(e =>
                    (R7Evaluation.IsPositive(e) || R7Evaluation.IsExcluded(e)) && e.Start >= c.Steps[0].Bucket
                    && e.End <= c.Steps[^1].Bucket && s.Bucket >= e.Start && s.Bucket <= e.End + R7Evaluation.LateMatch))));
        }).ToArray();

    private static void Write<T>(string name, T value, bool compact = false) =>
        File.WriteAllBytes(Path.Combine(Output, name), JsonSerializer.SerializeToUtf8Bytes(value,
            compact ? new JsonSerializerOptions(Json) { WriteIndented = false } : Json));
}
