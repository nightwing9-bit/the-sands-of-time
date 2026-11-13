using System.Runtime.InteropServices;
using Hosts.Worker;
using Microsoft.ML;
using Revenant.Intelligence;
using Revenant.Observations;
using Revenant.Synthetic;
using Xunit;

namespace Revenant.Tests.Intelligence.R7;

public sealed partial class R7Generator4DevelopmentTests
{
    private const int DailyBuckets = V4LongitudinalPlanner.BucketsPerDay;
    private const int CoreWarmup = 288;
    private const int CoreOrigin = SsaTrailingRate.FirstRate + CoreWarmup;
    private const string Exact = nameof(SsaTrailingRateDecoder.TrailingRateExactInverse);
    private const string Integrated = nameof(SsaTrailingRateDecoder.TrailingRateIntegratedExtrapolation);
    private const string Drift = "CalibratedDrift";

    [Fact]
    [Trait("Category", "R7Diagnostics")]
    public void Verify_G4_overlap_identity_on_all_authoritative_targets()
    {
        var (roots, output, manifest) = DiagnosticInput();
        long maxOracleMinorError = 0;
        long maxPersistenceMinorError = 0;
        var checkedTargets = 0;
        foreach (var root in roots)
        foreach (var bank in root.Banks)
        {
            var levels = Values(bank);
            var scale = SsaAdvisory.Scale(bank.ReferenceMinor!.Value, 1_000);
            for (var origin = CoreOrigin; origin < levels.Length; origin++)
            {
                var target = root.Targets[bank.SubjectId][origin];
                var oracle = new float[SsaAdvisory.Horizon];
                var zero = new float[SsaAdvisory.Horizon];
                for (var h = 1; h <= SsaAdvisory.Horizon; h++)
                {
                    if (target[h - 1] is not { } actual) continue;
                    var lagged = levels[origin + h - SsaTrailingRate.Window];
                    var actualRate = SsaTrailingRate.Rate(lagged, actual, scale);
                    var zeroRate = SsaTrailingRate.ZeroInnovationRate(levels, scale, origin, h);
                    var movement = checked(actual - levels[origin]);
                    var rateResidual = (actualRate - zeroRate) * (SsaTrailingRate.Window * (double)scale);
                    Assert.InRange(Math.Abs(movement - rateResidual), 0, 1e-6);
                    oracle[h - 1] = (float)actualRate;
                    zero[h - 1] = (float)zeroRate;
                    checkedTargets++;
                }
                var decoded = SsaTrailingRate.Decode(SsaTrailingRateDecoder.TrailingRateExactInverse, levels, scale, origin, oracle);
                var persistence = SsaTrailingRate.Decode(SsaTrailingRateDecoder.TrailingRateExactInverse, levels, scale, origin, zero);
                for (var h = 0; h < SsaAdvisory.Horizon; h++)
                {
                    if (target[h] is not { } actual) continue;
                    var oracleError = Math.Abs(decoded[h] - actual);
                    var zeroError = Math.Abs(persistence[h] - levels[origin]);
                    Assert.InRange(oracleError, 0, MinorUnitBound(oracle[h], scale));
                    Assert.InRange(zeroError, 0, MinorUnitBound(zero[h], scale));
                    maxOracleMinorError = Math.Max(maxOracleMinorError, oracleError);
                    maxPersistenceMinorError = Math.Max(maxPersistenceMinorError, zeroError);
                }
            }
        }
        Assert.True(checkedTargets > 0);
        R7RetainedPilotDiagnosticsTests.Write(output, "gate-s-overlap.json", new
        {
            ManifestSha256 = manifest, Roots = roots.Select(r => r.Manifest.Root.Key), CheckedTargets = checkedTargets,
            MaxOracleMinorError = maxOracleMinorError, MaxPersistenceMinorError = maxPersistenceMinorError,
        });

        static long MinorUnitBound(float rate, long scale)
        {
            var ulp = Math.Max((double)MathF.BitIncrement(rate) - rate, (double)rate - MathF.BitDecrement(rate));
            return (long)Math.Floor(.500001 + ulp * .5 * SsaTrailingRate.Window * scale);
        }
    }

    [Fact]
    [Trait("Category", "R7DiagnosticsNative")]
    public void Measure_G4_core_forecast_rotations()
    {
        Assert.True(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64);
        var (roots, output, manifest) = DiagnosticInput();
        var ml = new MLContext(seed: 7);
        var rotations = Enumerable.Range(0, roots.Length)
            .Select(i => Rotate(ml, roots[i], roots[(i + 1) % 3], roots[(i + 2) % 3], output)).ToArray();
        R7RetainedPilotDiagnosticsTests.Write(output, "gate-d-rotations.json", new
        {
            ManifestSha256 = manifest, Runtime = RuntimeInformation.FrameworkDescription,
            Platform = RuntimeInformation.RuntimeIdentifier, Rotations = rotations,
            PassingRotations = rotations.Count(r => r.Pass), Pass = rotations.Count(r => r.Pass) >= 2,
        });
    }

    private sealed record Origin(int Bucket, long Available, double RelativeSigma, int Group, float[] Rates,
        long[][] Paths, long?[] Targets, bool IsolatedDebit, LongitudinalEvent[] ActiveWindows, LongitudinalEvent[] Pressure, LongitudinalEvent[] Recovery,
        LongitudinalEvent[] Shock);

    private sealed record BankRows(string Bank, Origin[] Origins);

    private static readonly string[] Methods = ["Persistence", "TrailingMedian", "SeasonalNaive", Drift, "G4ChangeSsa", Exact, Integrated];
    private const int DriftIndex = 3;
    private const int ExactIndex = 5;
    private const int IntegratedIndex = 6;

    private sealed record Rotation(int Train, int Calibration, int Measurement, string TrainRoot, string CalibrationRoot,
        string MeasurementRoot, SsaForecastSpec RateSpec, SsaForecastSpec ControlSpec, string TrainingBank,
        string TrainingSeriesSha256, string ArtifactSha256, string ControlBank, string ControlSeriesSha256,
        string ControlArtifactSha256, double[] Edges, double[][][] Weights, string Selected, bool Pass,
        GateSummary[] CalibrationScores, GateSummary[] MeasurementScores, object[] ByBank, object ByHorizon, object RateSkill);

    private sealed record GateSummary(string Method, double OverallMae, double OverallRatio, double StressMae, double StressSkill,
        double DriftStressMae, double HighStressMae, double PersistenceHighStressMae, int HighStressChangedTargets, bool Pass);

    private static Rotation Rotate(MLContext ml, LongitudinalSample train, LongitudinalSample calibrate,
        LongitudinalSample measure, string output)
    {
        var oldSpec = R7QualificationEvaluationTests.ForecastCandidates[0].Spec;
        var spec = oldSpec with { TrainSize = train.Manifest.Seals - SsaTrailingRate.FirstRate, Warmup = CoreWarmup };
        var training = R7QualificationEvaluationTests.TrainingBank([train with
        {
            Banks = [.. train.Banks.Where(b => train.Events.Any(e => e.Subject == b.SubjectId && e.Kind == LongitudinalEventKind.DevelopingPressure))],
        }]);
        var control = R7QualificationEvaluationTests.TrainingBank([train]);
        var artifact = RevenantSsaInference.FitTrailingRate(ml, spec, training.ReferenceMinor!.Value, Values(training));
        var oldArtifact = RevenantSsaInference.FitForecast(ml, oldSpec, control.ReferenceMinor!.Value, Values(control), 0);
        var artifactDirectory = Path.Combine(output, "research-zips", $"train-{train.Manifest.Root.Index}");
        Directory.CreateDirectory(artifactDirectory);
        File.WriteAllBytes(Path.Combine(artifactDirectory, "trailing-rate.zip"), artifact);
        File.WriteAllBytes(Path.Combine(artifactDirectory, "g4-change.zip"), oldArtifact);

        var calibration = ReadRows(calibrate);
        var sigma = calibration.SelectMany(b => b.Origins.Select(o => o.RelativeSigma)).Order().ToArray();
        double[] edges = [sigma[(sigma.Length - 1) / 3], sigma[(sigma.Length - 1) * 2 / 3]];
        calibration = AssignGroups(calibration, edges);
        var weights = FitWeights(calibration);
        var calibrationScores = GateScores(calibration, weights);

        // Pick a decoder on Calibration, prioritizing the overall constraint and then developing-pressure error.
        var selected = calibrationScores.OrderByDescending(m => m.OverallRatio <= 1)
            .ThenBy(m => m.StressMae).ThenBy(m => m.Method, StringComparer.Ordinal).First().Method;
        var measurement = AssignGroups(ReadRows(measure), edges);
        var measurementScores = GateScores(measurement, weights);
        var all = measurement.SelectMany(b => b.Origins).ToArray();
        object HorizonMetrics(Origin[] origins, int h) => new
        {
            Minutes = (h + 1) * 5,
            Metrics = Methods.Select((method, m) => new
            {
                Method = method,
                Overall = Measure(origins, h, m, weights),
                Volatility = Enumerable.Range(0, 3).Select(g => Measure(origins.Where(o => o.Group == g), h, m, weights)),
                Developing = Measure(origins.Where(o => Overlaps(o, h, o.Pressure)), h, m, weights),
                Recovery = Measure(origins.Where(o => Overlaps(o, h, o.Recovery)), h, m, weights),
                Sudden = Measure(origins.Where(o => Overlaps(o, h, o.Shock)), h, m, weights),
                IsolatedDebitProxy = Measure(origins.Where(o => o.IsolatedDebit), h, m, weights),
                Active = Measure(origins.Where(o => o.Targets[h].HasValue && Active(o, h)), h, m, weights),
            }),
            DecoderMeanAbsoluteDifference = origins.Where(o => o.Targets[h].HasValue)
                .Average(o => Math.Abs((double)o.Paths[ExactIndex][h] - o.Paths[IntegratedIndex][h])),
        };
        var byBank = measurement.Select(b => (object)new
        {
            b.Bank, EligibleOrigins = b.Origins.Length,
            Horizons = Enumerable.Range(0, SsaAdvisory.Horizon).Select(h => HorizonMetrics(b.Origins, h)),
        }).ToArray();
        return new(train.Manifest.Root.Index, calibrate.Manifest.Root.Index, measure.Manifest.Root.Index,
            train.Manifest.Root.Key, calibrate.Manifest.Root.Key, measure.Manifest.Root.Key, spec, oldSpec, training.SubjectId,
            training.Digest, ObservationEncoding.Hash(artifact), control.SubjectId, control.Digest, ObservationEncoding.Hash(oldArtifact),
            edges, weights, selected, measurementScores.Single(s => s.Method == selected).Pass,
            calibrationScores, measurementScores, byBank, Enumerable.Range(0, SsaAdvisory.Horizon).Select(h => HorizonMetrics(all, h)),
            RateComparison(measure, measurement, spec));

        BankRows[] ReadRows(LongitudinalSample sample) => sample.Banks.Select(bank =>
        {
            var opening = bank.ReferenceMinor!.Value;
            var levels = Values(bank);
            var scale = SsaAdvisory.Scale(opening, spec.ScaleDivisor);
            var steps = RevenantSsaInference.ForecastTrailingRate(ml, artifact, spec, opening, levels);
            VerifyPrefix(ml, artifact, spec, opening, levels, steps);
            var oldSteps = RevenantSsaInference.ForecastBank(ml, oldArtifact, oldSpec, null, opening, levels);
            var (pressure, recovery, shock) = StressWindows(sample, bank.SubjectId);
            var activeWindows = sample.Events.Where(e => e.Subject == bank.SubjectId).ToArray();
            return new BankRows(bank.SubjectId, steps.Select(step =>
            {
                var t = step.Bucket;
                var current = levels[t];
                var horizons = Enumerable.Range(1, SsaAdvisory.Horizon);
                var daily = horizons.Select(h => checked(current + checked(levels[t + h - DailyBuckets] - levels[t - DailyBuckets]))).ToArray();
                var rate = SsaTrailingRate.Input(levels, opening, scale, t);
                var drift = SsaTrailingRate.Decode(SsaTrailingRateDecoder.TrailingRateIntegratedExtrapolation, levels,
                    scale, t, Enumerable.Repeat(rate, SsaAdvisory.Horizon).ToArray());
                var isolated = checked(levels[t - 1] - current) >= R7ForecastMetrics.ActiveChangeMinor
                    && Enumerable.Range(t - SsaTrailingRate.Window + 1, SsaTrailingRate.Window - 1)
                        .All(i => Math.Abs((double)levels[i] - levels[i - 1]) < R7ForecastMetrics.ActiveChangeMinor)
                    && !activeWindows.Any(e => t >= e.Start && t <= e.End);
                long[][] paths = [horizons.Select(_ => current).ToArray(),
                    horizons.Select(_ => R7Evaluation.TrailingMedian(levels, t)).ToArray(), daily, drift,
                    oldSteps[t - oldSpec.Warmup].Central, step.ExactInverse, step.IntegratedExtrapolation];
                return new Origin(t, current, step.Sigma / (double)scale, 0, step.Rates, paths, sample.Targets[bank.SubjectId][t],
                    isolated, activeWindows, pressure, recovery, shock);
            }).ToArray());
        }).ToArray();
    }

    private static (LongitudinalEvent[] Pressure, LongitudinalEvent[] Recovery, LongitudinalEvent[] Shock)
        StressWindows(LongitudinalSample sample, string bank)
    {
        var events = sample.Events.Where(e => e.Subject == bank).ToArray();
        var developing = events.Where(e => e.Kind == LongitudinalEventKind.DevelopingPressure).ToArray();
        // G4 records the drain on its source accounts and the recovery on the receiving accounts. The bank event spans both.
        var pressure = developing.Select(e => e with
        {
            End = sample.Events.Where(a => a.Mechanism == VelocityMechanism.TreasuryDrain && a.Start == e.Start
                && a.Subject.StartsWith(bank + "/", StringComparison.Ordinal)).Select(a => a.End).Distinct().Single(),
        }).ToArray();
        var recovery = developing.Select(e => e with
        {
            Start = sample.Events.Where(a => a.Mechanism == VelocityMechanism.TreasuryDrain && a.End == e.End && a.Start > e.Start)
                .Select(a => a.Start).Distinct().Single(),
        }).ToArray();
        return (pressure, recovery, events.Where(e => e.Kind == LongitudinalEventKind.SuddenShock)
            .Select(e => e with { End = e.Start }).ToArray());
    }

    private static bool Overlaps(Origin origin, int horizon, LongitudinalEvent[] windows) =>
        windows.Any(e => origin.Bucket + horizon + 1 >= e.Start && origin.Bucket < e.End);

    private static bool Active(Origin o, int h) =>
        Math.Abs((double)o.Targets[h]!.Value - o.Available) >= R7ForecastMetrics.ActiveChangeMinor
        || o.ActiveWindows.Any(e => o.Bucket + h + 1 >= e.Start && o.Bucket <= e.End);

    private static BankRows[] AssignGroups(BankRows[] banks, double[] edges) => banks.Select(b => b with
    {
        Origins = [.. b.Origins.Select(o => o with { Group = o.RelativeSigma <= edges[0] ? 0 : o.RelativeSigma <= edges[1] ? 1 : 2 })],
    }).ToArray();

    private static double[][][] FitWeights(BankRows[] calibration)
    {
        var origins = calibration.SelectMany(b => b.Origins).ToArray();
        return Enumerable.Range(0, Methods.Length).Select(m => Enumerable.Range(0, 3).Select(g =>
            Enumerable.Range(0, SsaAdvisory.Horizon).Select(h => m is DriftIndex or ExactIndex or IntegratedIndex
                ? SsaTrailingRate.FitWeight(origins.Where(o => o.Group == g && o.Targets[h].HasValue)
                    .Select(o => (checked(o.Targets[h]!.Value - o.Available), checked(o.Paths[m][h] - o.Available))))
                : 1d).ToArray()).ToArray()).ToArray();
    }

    private static long Predict(Origin o, int method, int h, double[][][] weights) =>
        SsaTrailingRate.Shrink(o.Available, o.Paths[method][h], weights[method][o.Group][h]);

    private sealed record ErrorMetrics(int Targets, double? Mae, double? Rmse, double? Bias, double? MeanPrediction,
        double? MeanRealized, double? StrongestBaselineMae, double? Ratio);

    private static ErrorMetrics Measure(IEnumerable<Origin> origins, int h, int method, double[][][] weights)
    {
        var rows = origins.Where(o => o.Targets[h].HasValue).ToArray();
        if (rows.Length == 0) return new(0, null, null, null, null, null, null, null);
        var errors = rows.Select(o => (double)Predict(o, method, h, weights) - o.Targets[h]!.Value).ToArray();
        var mae = errors.Average(Math.Abs);
        var strongest = Enumerable.Range(0, DriftIndex + 1).Min(m =>
            rows.Average(o => Math.Abs((double)Predict(o, m, h, weights) - o.Targets[h]!.Value)));
        return new(rows.Length, mae, Math.Sqrt(errors.Average(e => e * e)), errors.Average(),
            rows.Average(o => (double)Predict(o, method, h, weights) - o.Available),
            rows.Average(o => (double)o.Targets[h]!.Value - o.Available), strongest, strongest > 0 ? mae / strongest : null);
    }

    private static GateSummary[] GateScores(BankRows[] banks, double[][][] weights)
    {
        var origins = banks.SelectMany(b => b.Origins).ToArray();
        var horizons = Enumerable.Range(0, SsaAdvisory.Horizon).ToArray();
        double Overall(int m) => horizons.Average(h => Measure(origins, h, m, weights).Mae!.Value);
        double Stress(int m, bool highOnly = false) => horizons.Average(h => Measure(
            origins.Where(o => Overlaps(o, h, o.Pressure) && (!highOnly || o.Group == 2)), h, m, weights).Mae!.Value);
        var bestOverall = Enumerable.Range(0, DriftIndex + 1).Min(Overall);
        var persistenceStress = Stress(0);
        var driftStress = Stress(DriftIndex);
        var highPersistence = Stress(0, highOnly: true);
        return new[] { ExactIndex, IntegratedIndex }.Select(m =>
        {
            var overall = Overall(m);
            var stress = Stress(m);
            var skill = 1 - stress / persistenceStress;
            var high = Stress(m, highOnly: true);
            var changed = horizons.Sum(h => origins.Count(o => o.Group == 2 && o.Targets[h].HasValue && Overlaps(o, h, o.Pressure)
                && Predict(o, m, h, weights) != o.Available));
            return new GateSummary(Methods[m], overall, overall / bestOverall, stress, skill, driftStress, high, highPersistence,
                changed, overall <= bestOverall && skill >= .10 && stress < driftStress && changed > 0 && high < highPersistence);
        }).ToArray();
    }

    private static object RateComparison(LongitudinalSample sample, BankRows[] banks, SsaForecastSpec spec) => banks.Select(b =>
    {
        var series = sample.Banks.Single(s => s.SubjectId == b.Bank);
        var levels = Values(series);
        var scale = SsaAdvisory.Scale(series.ReferenceMinor!.Value, spec.ScaleDivisor);
        return new
        {
            b.Bank,
            Horizons = Enumerable.Range(0, SsaAdvisory.Horizon).Select(h =>
            {
                var rows = b.Origins.Where(o => o.Targets[h].HasValue).Select(o =>
                {
                    var anchor = levels[o.Bucket + h + 1 - SsaTrailingRate.Window];
                    var target = o.Targets[h]!.Value;
                    var actualRate = SsaTrailingRate.Rate(anchor, target, scale);
                    var zeroRate = SsaTrailingRate.ZeroInnovationRate(levels, scale, o.Bucket, h + 1);
                    return (SsaError: Math.Abs(o.Rates[h] - actualRate), ZeroError: Math.Abs(zeroRate - actualRate),
                        PredictedMovement: checked(o.Paths[ExactIndex][h] - o.Available), ActualMovement: checked(target - o.Available));
                }).ToArray();
                return new
                {
                    Minutes = (h + 1) * 5, Targets = rows.Length, SsaRateMae = rows.Average(x => x.SsaError),
                    ZeroInnovationRateMae = rows.Average(x => x.ZeroError), PredictedMovement = rows.Average(x => (double)x.PredictedMovement),
                    ActualMovement = rows.Average(x => (double)x.ActualMovement),
                    InnovationMae = rows.Average(x => Math.Abs((double)x.PredictedMovement - x.ActualMovement)),
                    ZeroInnovationMae = rows.Average(x => Math.Abs((double)x.ActualMovement)),
                };
            }),
        };
    }).ToArray();

    private static void VerifyPrefix(MLContext ml, byte[] artifact, SsaForecastSpec spec, long opening, long[] levels,
        RevenantSsaInference.TrailingRateStep[] steps)
    {
        Assert.Empty(RevenantSsaInference.ForecastTrailingRate(ml, artifact, spec, opening, levels[..CoreOrigin]));
        var prefix = RevenantSsaInference.ForecastTrailingRate(ml, artifact, spec, opening, levels[..(CoreOrigin + 1)]).Single();
        Assert.Equal(steps[0].Rates, prefix.Rates);
        Assert.Equal(steps[0].ExactInverse, prefix.ExactInverse);
        Assert.Equal(steps[0].IntegratedExtrapolation, prefix.IntegratedExtrapolation);

        // Same artifact, different leading state, same final 288 complete rates.
        var altered = levels[..(CoreOrigin + 2)];
        altered[0] = checked(altered[0] + 123_457);
        var restarted = RevenantSsaInference.ForecastTrailingRate(ml, artifact, spec, opening, altered)[^1];
        Assert.Equal(steps[1].Rates, restarted.Rates);
        Assert.Equal(steps[1].ExactInverse, restarted.ExactInverse);
    }
}
