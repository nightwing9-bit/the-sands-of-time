using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Hosts.Worker;
using Microsoft.ML;
using Revenant.Intelligence;
using Revenant.Observations;
using Revenant.Synthetic;
using Xunit;

namespace Revenant.Tests.Intelligence.R7;

public sealed partial class R7Generator4DevelopmentTests
{
    private static (LongitudinalSample[] Roots, string Output, string Evidence, string Manifest, JsonElement[] Prior) LearningInput()
    {
        string Require(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"Set {name}.");
        var input = Require("REVENANT_R7_G5_INPUT");
        var output = Require("REVENANT_R7_DIAGNOSTICS_OUTPUT");
        var evidence = Require("REVENANT_R7_DRAIN_EVIDENCE");
        Directory.CreateDirectory(output);
        var hash = ObservationEncoding.Hash(File.ReadAllBytes(Path.Combine(input, "pilot.json")));
        Assert.Equal("e6286648544f3c624db7f0cafd9e508113da208a225b3a7efc071505d9661e05", hash);
        var manifest = LongitudinalSample.ReadManifest(input, "pilot.json");
        Assert.Equal(V4LongitudinalPlannerG5.Roots(), manifest.Roots.Select(r => r.Root));
        using var report = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(evidence, "d3-executed-ssa.json")));
        Assert.Equal(hash, report.RootElement.GetProperty("ManifestSha256").GetString());
        return (manifest.Roots.Select(r => LongitudinalSample.Read(input, r, requirePilotTargets: true, readFlows: true)).ToArray(),
            output, evidence, hash, report.RootElement.GetProperty("Rotations").EnumerateArray().Select(r => r.Clone()).ToArray());
    }

    private static byte[] OldArtifact(string evidence, int i, JsonElement results)
    {
        var zip = File.ReadAllBytes(Path.Combine(evidence, $"g5-root-{i}-three-bucket.zip"));
        ObservationEncoding.RequireDigest(zip, results.GetProperty("Artifact").GetString()!);
        return zip;
    }

    private static SsaForecastSpec ThreeBucketSpec(int trainSize) =>
        R7QualificationEvaluationTests.ForecastCandidates[0].Spec with { TrainSize = trainSize };

    [Fact]
    [Trait("Category", "R7Diagnostics")]
    public void Audit_exact_G5_SSA_fit_exposure()
    {
        var (roots, output, evidence, manifest, prior) = LearningInput();
        var rotations = roots.Select((train, i) =>
        {
            var old = prior[i].GetProperty("Results");
            Assert.Equal(train.Manifest.Root.Key, old.GetProperty("Train").GetString());
            var bank = train.Banks.Single(b => b.SubjectId == old.GetProperty("TrainingBank").GetString());
            Assert.Equal(bank.Digest, prior[i].GetProperty("TrainingSeriesSha256").GetString());
            var levels = Values(bank);
            var opening = bank.ReferenceMinor!.Value;
            var threshold = Materiality(train);
            var baselineTraining = DrainRows(train, threshold);
            var (drains, recovery, _) = StressWindows(train, bank.SubjectId);
            var events = train.Events.Where(e => e.Subject == bank.SubjectId).ToArray();
            var episodes = events.Where(e => e.Kind == LongitudinalEventKind.DevelopingPressure).Select(e =>
            {
                var minimum = levels[e.Start..(e.End + 1)].Min();
                return new { e.Start, e.End, Minimum = minimum, Crossing = minimum < opening / 10,
                    NearMiss = minimum >= opening / 10 && minimum <= opening * 13 / 100 };
            }).ToArray();
            const int first = 2;
            var observations = Enumerable.Range(first, levels.Length - first).ToArray();
            bool Includes(int t, IEnumerable<LongitudinalEvent> windows) => windows.Any(e => t >= e.Start && t <= e.End);
            bool Active(int t) => t >= ContinuationWindow && Observe(levels, opening, t, threshold).Active;
            var zip = OldArtifact(evidence, i, old);
            var model = SavedModelParameters(zip, ThreeBucketSpec(observations.Length));
            var weights = old.GetProperty("SsaWeights").Deserialize<double[]>()!;
            return new
            {
                Train = train.Manifest.Root.Key, Bank = bank.SubjectId, SeriesSha256 = bank.Digest, ArtifactSha256 = ObservationEncoding.Hash(zip),
                Seals = levels.Length, FirstFitSeal = first, FitObservations = observations.Length, Episodes = episodes,
                CrossingEpisodes = episodes.Count(e => e.Crossing), NearMissEpisodes = episodes.Count(e => e.NearMiss),
                Active = observations.Count(Active), ActiveDuringDrain = observations.Count(t => Active(t) && Includes(t, drains)),
                OrdinaryHardNegatives = observations.Count(t => !Includes(t, events) && (levels[t - 1] - levels[t]) / (double)opening >= threshold),
                DrainPercent = 100.0 * observations.Count(t => Includes(t, drains)) / observations.Length,
                FullEpisodePercent = 100.0 * observations.Count(t => episodes.Any(e => t >= e.Start && t <= e.End)) / observations.Length,
                RecoveryBuckets = observations.Count(t => Includes(t, recovery)), Threshold = threshold,
                Model = model,
                Baseline = new { Banks = baselineTraining.Select(r => r.Bank).Distinct().Count(), Origins = baselineTraining.Length,
                    OneStepTargets = baselineTraining.Count(r => r.Future[0].HasValue), Active = baselineTraining.Count(r => r.State.Active),
                    PressureEpisodes = train.Events.Count(e => e.Kind == LongitudinalEventKind.DevelopingPressure) },
                OldWeights = weights, ZeroWeights = weights.Count(w => w == 0),
                OldMeasurementError = old.GetProperty("Overall").EnumerateArray().Select(h =>
                    h.GetProperty("Metrics").EnumerateArray().Where(m => m.GetProperty("Method").GetString() is "GatedSsa" or "UngatedSsa")
                    .Select(m =>
                    {
                        var all = m.GetProperty("Overall");
                        var inside = m.GetProperty("Continuation");
                        var n = all.GetProperty("Targets").GetInt32();
                        var a = inside.GetProperty("Targets").GetInt32();
                        var mae = inside.GetProperty("Mae").GetDouble();
                        return new { Method = m.GetProperty("Method"), InsideTargets = a, InsideMae = mae, OutsideTargets = n - a,
                            OutsideMae = (n * all.GetProperty("Mae").GetDouble() - a * mae) / (n - a) };
                    }).ToArray()).ToArray(),
            };
        }).ToArray();
        R7RetainedPilotDiagnosticsTests.Write(output, "e0-training-exposure.json", new
        {
            ManifestSha256 = manifest, Decision = "CONFIRMED TRAINING-DESIGN DEFICIENCY", Rotations = rotations,
        });
    }

    private sealed record SavedSsaParameters(int Rank, int SeriesLength, int Window, int TrainSize);

    // ML.NET 5.0.1 AdaptiveSingularSpectrumSequenceModeler stores these four Int32 fields at the model payload start.
    private static SavedSsaParameters SavedModelParameters(byte[] zip, SsaForecastSpec spec)
    {
        using var archive = new ZipArchive(new MemoryStream(zip, writable: false));
        using var model = new BinaryReader(archive.GetEntry("TransformerChain/SSA/Model.key")!.Open());
        var header = model.ReadBytes(256);
        Assert.Equal("SSAMODLR"u8.ToArray(), header[64..72]);
        Assert.Equal(256L, System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(16)));
        var seriesLength = model.ReadInt32();
        var window = model.ReadInt32();
        var trainSize = model.ReadInt32();
        var rank = model.ReadInt32();
        Assert.Equal((spec.SeriesLength, spec.Window, spec.TrainSize), (seriesLength, window, trainSize));
        Assert.InRange(rank, 1, window - 1);
        return new(rank, seriesLength, window, trainSize);
    }

    private static LongitudinalEvent[] NearMissEvents(LongitudinalSample root, V4AdvisorySeries bank)
    {
        var levels = Values(bank);
        var opening = bank.ReferenceMinor!.Value;
        return root.Events.Where(e => e.Subject == bank.SubjectId && e.Kind == LongitudinalEventKind.DevelopingPressure)
            .Where(e => { var min = levels[e.Start..(e.End + 1)].Min(); return min >= opening / 10 && min <= opening * 13 / 100; }).ToArray();
    }

    private static double[][] LearnWeights(DrainRow[] calibration, Func<DrainRow, int, long> baseline, Func<DrainRow, int, long> correction) =>
        new[] { false, true }.Select(active => Enumerable.Range(0, SsaAdvisory.Horizon).Select(h =>
            SsaTrailingRate.FitWeight(calibration.Where(r => r.State.Active == active && r.Future[h].HasValue)
                .Select(r => (checked(r.Future[h]!.Value - baseline(r, h)), correction(r, h))))).ToArray()).ToArray();

    private static object LearningScores(LongitudinalSample measure, DrainRow[] rows, string[] methods, Func<DrainRow, int, int, long> predict)
    {
        var misses = measure.Banks.ToDictionary(b => b.SubjectId, b => NearMissEvents(measure, b));
        return new
        {
            Root = measure.Manifest.Root.Key, BankCount = measure.Banks.Length,
            States = ReportScores(rows, methods, predict),
            Additional = Enumerable.Range(0, SsaAdvisory.Horizon).Select(h => methods.Select((method, m) => new
            {
                Method = method, NonContinuation = Score(rows.Where(r => !r.State.Active), h, m, predict),
                NearMiss = Score(rows.Where(r => OverlapsWindow(r, h, misses[r.Bank])), h, m, predict),
                FalseExtrapolationAfterIsolatedDebit = rows.Count(r => r.Isolated && r.Future[h] >= r.Current && predict(r, m, h) < r.Current),
            }).ToArray()).ToArray(),
        };
    }

    [Fact]
    [Trait("Category", "R7DiagnosticsNative")]
    public void Replay_frozen_G5_SSA_increment_over_median()
    {
        Assert.True(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64);
        var (roots, output, evidence, manifest, prior) = LearningInput();
        var ml = new MLContext(seed: 7);
        var rotations = roots.Select((train, i) =>
        {
            var cal = roots[(i + 1) % roots.Length];
            var measure = roots[(i + 2) % roots.Length];
            var old = prior[i].GetProperty("Results");
            Assert.Equal(cal.Manifest.Root.Key, old.GetProperty("Calibration").GetString());
            Assert.Equal(measure.Manifest.Root.Key, old.GetProperty("Measurement").GetString());
            var threshold = Materiality(train);
            var medians = FitMedian(DrainRows(train, threshold));
            var oldWeights = old.GetProperty("SsaWeights").Deserialize<double[]>()!;
            var zip = OldArtifact(evidence, i, old);
            var spec = ThreeBucketSpec(prior[i].GetProperty("Spec").GetProperty("TrainSize").GetInt32());
            var calibration = DrainRows(cal, threshold);
            var measurement = DrainRows(measure, threshold);
            var calPaths = Replay(cal);
            var paths = Replay(measure);
            var weights = LearnWeights(calibration, Baseline, (r, h) => checked(Old(calPaths, r, h) - Baseline(r, h)));
            var rawWeights = LearnWeights(calibration, Baseline, (r, h) => checked(calPaths[r.Bank][r.Bucket][h] - Baseline(r, h)));
            return new
            {
                Train = train.Manifest.Root.Key, Calibration = cal.Manifest.Root.Key, Weights = weights, RawSensitivityWeights = rawWeights,
                ArtifactSha256 = ObservationEncoding.Hash(zip),
                Scores = LearningScores(measure, measurement, ["ConditionalMedian", "OldGatedSsa", "ConvexBlend", "OldUngatedSsa", "UngatedConvexBlend"], Predict),
            };

            Dictionary<string, Dictionary<int, long[]>> Replay(LongitudinalSample root) => root.Banks.ToDictionary(b => b.SubjectId,
                b => RevenantSsaInference.ForecastTrailingRate(ml, zip, spec, b.ReferenceMinor!.Value, Values(b), 3)
                    .ToDictionary(p => p.Bucket, p => p.ExactInverse));
            long Baseline(DrainRow r, int h) => MedianPrediction(r, h, medians);
            long Old(Dictionary<string, Dictionary<int, long[]>> replay, DrainRow r, int h) => r.State.Active
                ? SsaTrailingRate.Shrink(r.Current, replay[r.Bank][r.Bucket][h], oldWeights[h]) : r.Current;
            long Predict(DrainRow r, int m, int h) => m switch
            {
                0 => Baseline(r, h),
                1 => Old(paths, r, h),
                2 => SsaTrailingRate.Shrink(Baseline(r, h), Old(paths, r, h), weights[r.State.Active ? 1 : 0][h]),
                3 => paths[r.Bank][r.Bucket][h],
                4 => SsaTrailingRate.Shrink(Baseline(r, h), paths[r.Bank][r.Bucket][h], rawWeights[r.State.Active ? 1 : 0][h]),
                _ => throw new ArgumentOutOfRangeException(nameof(m)),
            };
        }).ToArray();
        R7RetainedPilotDiagnosticsTests.Write(output, "e1-frozen-ssa-blend.json", new
        {
            ManifestSha256 = manifest, Platform = RuntimeInformation.RuntimeIdentifier, Rotations = rotations,
        });
    }

    [Fact]
    [Trait("Category", "R7Diagnostics")]
    public void Check_one_enriched_G5_training_plan_before_execution()
    {
        var (frozen, output, _, manifest, _) = LearningInput();
        foreach (var root in frozen)
            Assert.Equal(root.Manifest.BehaviorSha256, PlanSample(root.Manifest.Root).Manifest.BehaviorSha256);
        var planned = PlanSample(V4LongitudinalPlannerG5.ResidualTrainingRoot(), residualTraining: true);
        var bank = planned.Banks.Single(b => b.SubjectId == V4LongitudinalPlannerG5.ResidualTrainingBank);
        var threshold = Materiality(planned with { Banks = planned.Banks.Where(b => b.SubjectId != bank.SubjectId).ToArray() });
        var coverage = TrainingCoverage(planned, bank, threshold);
        var rotationCoverage = frozen.Select(r =>
        {
            var trainThreshold = Materiality(r);
            return new { Train = r.Manifest.Root.Key, Threshold = trainThreshold, Coverage = TrainingCoverage(planned, bank, trainThreshold) };
        }).ToArray();
        R7RetainedPilotDiagnosticsTests.Write(output, "e3-training-plan.json", new
        {
            FrozenMeasurementManifestSha256 = manifest, Variant = V4LongitudinalPlannerG5.ResidualTrainingVariant,
            planned.Manifest, Bank = bank.SubjectId, Threshold = threshold, Coverage = coverage, RotationCoverage = rotationCoverage,
        });
        RequireExposure(coverage);
        Assert.All(rotationCoverage, r => RequireExposure(r.Coverage));
    }

    private sealed record EpisodeCoverage(int Start, int End, double MinimumFraction, bool Crossing, bool NearMiss);
    private sealed record FitCoverage(int Seals, int FitObservations, EpisodeCoverage[] Episodes, int HardNegatives, int ContinuationBuckets);

    private static FitCoverage TrainingCoverage(LongitudinalSample root, V4AdvisorySeries bank, double threshold)
    {
        var levels = Values(bank);
        var opening = bank.ReferenceMinor!.Value;
        var labels = root.Events.Where(e => e.Subject == bank.SubjectId).ToArray();
        var episodes = labels.Where(e => e.Kind == LongitudinalEventKind.DevelopingPressure)
            .Where(e => e.Start >= FirstResidual && e.End < levels.Length).OrderBy(e => e.Start).Select(e =>
            {
                var min = levels[e.Start..(e.End + 1)].Min();
                var crossing = Enumerable.Range(e.Start, e.End - e.Start + 1)
                    .Any(t => levels[t - 1] >= opening / 10 && levels[t] < opening / 10);
                return new EpisodeCoverage(e.Start, e.End, min / (double)opening, crossing, min >= opening / 10 && min <= opening * 13 / 100);
            }).ToArray();
        var observations = Enumerable.Range(FirstResidual, levels.Length - FirstResidual).ToArray();
        return new(levels.Length, observations.Length, episodes, observations.Count(t =>
            !labels.Any(e => e.Start <= t && t <= e.End) && (levels[t - 1] - levels[t]) / (double)opening >= threshold),
            observations.Count(t => Observe(levels, opening, t, threshold).Active));
    }

    private static void RequireExposure(FitCoverage exposure)
    {
        Assert.True(exposure.Episodes.Count(e => e.Crossing) >= 4, "Training needs four committed (or planned for feasibility) crossings.");
        Assert.True(exposure.Episodes.Count(e => e.NearMiss) >= 4, "Training needs four committed (or planned for feasibility) near misses.");
        Assert.True(exposure.HardNegatives >= 2, "Training needs multiple ordinary large-debit hard negatives.");
        Assert.All(exposure.Episodes.Zip(exposure.Episodes.Skip(1)), pair => Assert.True(pair.First.End < pair.Second.Start));
    }

    private const int FirstResidual = ContinuationWindow + 1;
    private const int ResidualScaleDivisor = 1_000;

    private static float[] Residuals(V4AdvisorySeries bank, double threshold, double[][] medians)
    {
        var levels = Values(bank);
        var opening = bank.ReferenceMinor!.Value;
        var scale = SsaAdvisory.Scale(opening, ResidualScaleDivisor);
        return Enumerable.Range(FirstResidual, levels.Length - FirstResidual).Select(t =>
        {
            var active = Observe(levels, opening, t - 1, threshold).Active;
            var predicted = SsaTrailingRate.AddMovement(levels[t - 1], opening * medians[active ? 1 : 0][0]);
            return (float)(checked(levels[t] - predicted) / (double)scale);
        }).ToArray();
    }

    private static long SimpleResidualMovement(float[] residuals, long scale, int t, int h, bool seasonal)
    {
        var index = t - FirstResidual;
        double sum = 0;
        for (var j = 0; j <= h; j++)
            sum += seasonal ? residuals[index + 1 + j - DailyBuckets] : residuals[index];
        return SsaTrailingRate.AddMovement(0, sum * scale);
    }

    [Fact]
    [Trait("Category", "R7Diagnostics")]
    public void Test_causal_G5_residual_learnability()
    {
        var (roots, output, _, manifest, _) = LearningInput();
        var rotations = roots.Select((train, i) =>
        {
            var cal = roots[(i + 1) % roots.Length];
            var measure = roots[(i + 2) % roots.Length];
            var threshold = Materiality(train);
            var training = DrainRows(train, threshold);
            var medians = FitMedian(training);
            var calibration = DrainRows(cal, threshold);
            var measurement = DrainRows(measure, threshold);
            var calBanks = cal.Banks.ToDictionary(b => b.SubjectId, b => (Bank: b, Residuals: Residuals(b, threshold, medians)));
            var measureBanks = measure.Banks.ToDictionary(b => b.SubjectId, b => (Bank: b, Residuals: Residuals(b, threshold, medians)));
            var recentWeights = LearnWeights(calibration, Baseline, (r, h) => Correction(calBanks, r, h, false));
            var seasonalWeights = LearnWeights(calibration, Baseline, (r, h) => Correction(calBanks, r, h, true));
            var stats = train.Banks.Select(bank =>
            {
                // For this bank's training feasibility diagnostic, all fitted coefficients and the threshold exclude this bank.
                var others = train with { Banks = train.Banks.Where(b => b.SubjectId != bank.SubjectId).ToArray() };
                var independentThreshold = Materiality(others);
                var independentMedian = FitMedian(DrainRows(others, independentThreshold));
                return Statistics(train, bank, Residuals(bank, independentThreshold, independentMedian), independentThreshold);
            }).ToArray();
            string[] methods = ["ConditionalMedian", "ResidualPersistence", "ResidualDaily"];
            return new
            {
                Train = train.Manifest.Root.Key, Calibration = cal.Manifest.Root.Key, Measurement = measure.Manifest.Root.Key,
                Threshold = threshold, Medians = medians, RecentWeights = recentWeights, SeasonalWeights = seasonalWeights,
                TrainLeaveOneBankOut = stats,
                MeasurementBankStatistics = measureBanks.Values.Select(b => Statistics(measure, b.Bank, b.Residuals, threshold)),
                Scores = LearningScores(measure, measurement, methods, Predict),
                ByBank = measurement.GroupBy(r => r.Bank).Select(g => new
                {
                    Bank = g.Key, Scores = Enumerable.Range(0, SsaAdvisory.Horizon).Select(h => methods.Select((name, m) => new
                    {
                        Method = name, Overall = Score(g, h, m, Predict).Mae,
                        Continuation = Score(g.Where(r => r.State.Active), h, m, Predict).Mae,
                        Drain = Score(g.Where(r => r.State.Active && InWindow(r, r.Drain)), h, m, Predict).Mae,
                    })),
                }),
            };

            long Baseline(DrainRow r, int h) => MedianPrediction(r, h, medians);
            long Predict(DrainRow r, int m, int h) => m == 0 ? Baseline(r, h) : SsaTrailingRate.AddMovement(Baseline(r, h),
                Correction(measureBanks, r, h, m == 2) * (m == 2 ? seasonalWeights : recentWeights)[r.State.Active ? 1 : 0][h]);
        }).ToArray();
        R7RetainedPilotDiagnosticsTests.Write(output, "e3-residual-feasibility.json", new
        {
            ManifestSha256 = manifest, FirstResidual, ScaleDivisor = ResidualScaleDivisor, Rotations = rotations,
        });
    }

    private static long Correction(Dictionary<string, (V4AdvisorySeries Bank, float[] Residuals)> banks, DrainRow r, int h, bool seasonal)
    {
        var (bank, residuals) = banks[r.Bank];
        return SimpleResidualMovement(residuals, SsaAdvisory.Scale(bank.ReferenceMinor!.Value, ResidualScaleDivisor), r.Bucket, h, seasonal);
    }

    private static object Statistics(LongitudinalSample root, V4AdvisorySeries bank, float[] residuals, double threshold)
    {
        var rows = DrainRows(root with { Banks = [bank] }, threshold);
        bool OrdinarySettlement(int t) => root.Flows![bank.SubjectId][t] is { Returns: 0, NetSettled: not 0 } now
            && root.Flows[bank.SubjectId][t - 1] is { } previous && now.Balance.Reserved == previous.Balance.Reserved
            && !root.Events.Any(e => e.Subject == bank.SubjectId && e.Start <= t && t <= e.End);
        float At(int t) => residuals[t - FirstResidual];
        int[] lags = [1, 2, 3, 4, 8, 12, 24, 287, 288, 289];
        return new
        {
            Bank = bank.SubjectId, SeriesSha256 = bank.Digest,
            Lags = lags.Select(lag =>
            {
                var pairs = Enumerable.Range(FirstResidual + lag, residuals.Length - lag).ToArray();
                return new { Lag = lag, All = Pearson(pairs.Select(t => ((double)At(t - lag), (double)At(t)))),
                    OrdinarySettlement = Pearson(pairs.Where(t => OrdinarySettlement(t) && OrdinarySettlement(t - lag))
                        .Select(t => ((double)At(t - lag), (double)At(t)))) };
            }),
            Outcomes = new[] { "Ordinary", "Continuation", "Drain", "Isolated", "Recovery" }.Select(state =>
            {
                var next = rows.Where(r => r.Future[0].HasValue && (state switch
                {
                    "Ordinary" => !r.State.Active && !OverlapsWindow(r, 0, r.Stress),
                    "Continuation" => r.State.Active,
                    "Drain" => r.State.Active && InWindow(r, r.Drain),
                    "Isolated" => r.Isolated,
                    _ => OverlapsWindow(r, 0, r.Recovery),
                })).Select(r => (Prior: (double)At(r.Bucket), Next: (double)At(r.Bucket + 1))).ToArray();
                return new { State = state, Count = next.Length, Mean = next.Length == 0 ? (double?)null : next.Average(x => x.Next),
                    Mae = next.Length == 0 ? (double?)null : next.Average(x => Math.Abs(x.Next)), Association = Pearson(next) };
            }),
        };
    }

    private const string ResidualArchitecture = "g5-conditional-median-plus-ssa-residual/1";
    private const string BaselineArchitecture = "g5-conditional-median-drain-state/1";

    private static (LongitudinalSample Sample, string Hash, string Revision) ReadEnrichedTraining()
    {
        var trainingDirectory = Environment.GetEnvironmentVariable("REVENANT_R7_RESIDUAL_TRAIN")
            ?? throw new InvalidOperationException("Set REVENANT_R7_RESIDUAL_TRAIN.");
        var manifestBytes = File.ReadAllBytes(Path.Combine(trainingDirectory, "residual-train.json"));
        var trainingManifestHash = ObservationEncoding.Hash(manifestBytes);
        using var provenance = JsonDocument.Parse(manifestBytes);
        Assert.Equal(V4LongitudinalPlannerG5.ResidualTrainingVariant, provenance.RootElement.GetProperty("TrainingVariant").GetString());
        var source = LongitudinalSample.ReadManifest(trainingDirectory, "residual-train.json");
        Assert.Equal(V4LongitudinalPlannerG5.Version, source.Generator);
        var identity = Assert.Single(source.Roots);
        Assert.Equal(V4LongitudinalPlannerG5.ResidualTrainingRoot(), identity.Root);
        var enriched = LongitudinalSample.Read(trainingDirectory, identity, readFlows: true);
        return (enriched, trainingManifestHash, source.Revision);
    }

    [Fact]
    [Trait("Category", "R7Diagnostics")]
    public void Verify_committed_G5_training_exposure_before_fitting()
    {
        var (roots, output, _, manifest, _) = LearningInput();
        var (training, trainingManifestHash, revision) = ReadEnrichedTraining();
        var bank = training.Banks.Single(b => b.SubjectId == V4LongitudinalPlannerG5.ResidualTrainingBank);
        var exposure = roots.Select(r =>
        {
            var threshold = Materiality(r);
            return new { Train = r.Manifest.Root.Key, Threshold = threshold, Coverage = TrainingCoverage(training, bank, threshold) };
        }).ToArray();
        R7RetainedPilotDiagnosticsTests.Write(output, "e3-committed-training-exposure.json", new
        {
            ManifestSha256 = manifest, TrainingManifestSha256 = trainingManifestHash, Revision = revision,
            training.Manifest, Bank = bank.SubjectId, SeriesSha256 = bank.Digest, Exposure = exposure,
        });
        Assert.All(exposure, r => RequireExposure(r.Coverage));
    }

    [Fact]
    [Trait("Category", "R7DiagnosticsNative")]
    public void Qualify_incremental_SSA_residual_on_frozen_G5()
    {
        Assert.True(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64);
        var (roots, output, _, manifest, _) = LearningInput();
        var (enriched, trainingManifestHash, revision) = ReadEnrichedTraining();
        Assert.DoesNotContain(roots, r => r.Manifest.Root.Key == enriched.Manifest.Root.Key);
        var trainingBank = enriched.Banks.Single(b => b.SubjectId == V4LongitudinalPlannerG5.ResidualTrainingBank);
        var ml = new MLContext(seed: 7);
        var rotations = roots.Select((train, i) =>
        {
            var cal = roots[(i + 1) % roots.Length];
            var measure = roots[(i + 2) % roots.Length];
            var threshold = Materiality(train);
            var baselineTraining = DrainRows(train, threshold);
            var medians = FitMedian(baselineTraining);
            var coverage = TrainingCoverage(enriched, trainingBank, threshold);
            RequireExposure(coverage); // Check actual bank exposure before fitting.
            var trainingResiduals = Residuals(trainingBank, threshold, medians);
            // Retain D3 window/series and exact-rank selection. Change only the input representation and actual train size.
            var spec = ThreeBucketSpec(trainingResiduals.Length) with { Warmup = CoreOrigin - FirstResidual };
            var zip = RevenantSsaInference.FitResidualForecast(ml, spec, trainingResiduals);
            var artifactPath = Path.Combine(output, $"e4-residual-rotation-{i}.zip");
            File.WriteAllBytes(artifactPath, zip);
            var hash = ObservationEncoding.Hash(zip);
            var residualEncoding = ObservationEncoding.Begin(ResidualArchitecture).WriteCount(trainingResiduals.Length);
            foreach (var residual in trainingResiduals) residualEncoding.WriteInt32(BitConverter.SingleToInt32Bits(residual));
            var residualHash = ObservationEncoding.Hash(residualEncoding.ToArray());
            var saved = File.ReadAllBytes(artifactPath);
            ObservationEncoding.RequireDigest(saved, hash);
            var calibration = DrainRows(cal, threshold);
            var measurement = DrainRows(measure, threshold);
            var calPaths = Paths(cal);
            var paths = Paths(measure);
            var weights = LearnContinuousWeights(calibration, Baseline, (r, h) => Movement(calPaths, r, h));
            var calBanks = cal.Banks.ToDictionary(b => b.SubjectId, b => (Bank: b, Residuals: Residuals(b, threshold, medians)));
            var measureBanks = measure.Banks.ToDictionary(b => b.SubjectId, b => (Bank: b, Residuals: Residuals(b, threshold, medians)));
            var recentWeights = LearnWeights(calibration, Baseline, (r, h) => Correction(calBanks, r, h, false));
            var seasonalWeights = LearnWeights(calibration, Baseline, (r, h) => Correction(calBanks, r, h, true));
            var levels = measure.Banks.ToDictionary(b => b.SubjectId, Values);
            string[] methods = ["Persistence", "TrailingMedian", "SeasonalNaive", "ConditionalMedian", "ResidualPersistence",
                "ResidualDaily", "ResidualSsa"];
            double Average(int method, Func<DrainRow, int, bool> include) => Enumerable.Range(0, SsaAdvisory.Horizon)
                .Average(h => Score(measurement.Where(r => include(r, h)), h, method, Predict).Mae!.Value);
            var overall = Enumerable.Range(0, methods.Length).Select(m => Average(m, (_, _) => true)).ToArray();
            var drain = Enumerable.Range(0, methods.Length).Select(m => Average(m, (r, _) => r.State.Active && InWindow(r, r.Drain))).ToArray();
            var developing = Enumerable.Range(0, methods.Length).Select(m => Average(m, (r, h) => OverlapsWindow(r, h, r.Drain))).ToArray();
            var strongest = overall[..^1].Min();
            var bestMatchedDrain = drain[3..^1].Min();
            // Pinned before running E4: use a 1% minimum effect for incremental skill, above the negligible old +0.076%.
            const double incrementalFloor = .01;
            var gate = new
            {
                OverallRatio = overall[^1] / strongest,
                OverallIncrement = 1 - overall[^1] / overall[3..^1].Min(),
                DrainIncrement = 1 - drain[^1] / bestMatchedDrain,
                DrainSkillAgainstPersistence = 1 - drain[^1] / drain[0],
                DevelopingSkillAgainstPersistence = 1 - developing[^1] / developing[0],
                NonzeroActiveHorizons = weights[1].Count(w => w > 0),
                Pass = overall[^1] <= strongest && drain[^1] <= .9 * drain[0] && developing[^1] <= .9 * developing[0]
                    && drain[^1] <= (1 - incrementalFloor) * bestMatchedDrain
                    && overall[^1] <= (1 - incrementalFloor) * overall[3..^1].Min()
                    && weights[1].Count(w => w > 0) >= 2,
            };
            var parameters = new
            {
                Architecture = ResidualArchitecture, Baseline = BaselineArchitecture, TrainingManifestSha256 = trainingManifestHash,
                TrainingSeriesSha256 = trainingBank.Digest, ResidualSeriesSha256 = residualHash,
                BaselineTrainingRoot = train.Manifest.Root.Key, BaselineBanks = train.Banks.Select(b => new { b.SubjectId, b.Digest }).ToArray(),
                BaselineOrigins = baselineTraining.Length, Threshold = threshold, Medians = medians,
                Normalization = "max(1, openingMinor / 1000), integer division", FirstResidual,
                Spec = spec, FittedModel = SavedModelParameters(saved, spec), SsaArtifactSha256 = hash, CalibrationRoot = cal.Manifest.Root.Key, Weights = weights,
                CalibrationObjective = "clipped weighted median for level MAE; absolute correction weights",
                VolatilityGroup = "causal continuation: at least two material debits in four completed intervals and negative net change",
            };
            var pin = JsonSerializer.SerializeToUtf8Bytes(parameters);
            File.WriteAllBytes(Path.Combine(output, $"e4-residual-rotation-{i}-pin.json"), pin);
            return new
            {
                Parameters = parameters, PinSha256 = ObservationEncoding.Hash(pin), Coverage = coverage, Gate = gate,
                OverallMae = overall, ContinuingDrainMae = drain, DevelopingMae = developing, RecentWeights = recentWeights,
                SeasonalWeights = seasonalWeights, Methods = methods,
                Scores = LearningScores(measure, measurement, methods, Predict),
                Corrections = Enumerable.Range(0, SsaAdvisory.Horizon).Select(h =>
                {
                    var rows = measurement.Where(r => r.Future[h].HasValue).ToArray();
                    return new { Horizon = h + 1, MeanAbsoluteCorrection = rows.Average(r => Math.Abs((double)Predict(r, 6, h) - Baseline(r, h))),
                        MeanAbsoluteActiveCorrection = rows.Where(r => r.State.Active).Average(r => Math.Abs((double)Predict(r, 6, h) - Baseline(r, h))) };
                }).ToArray(),
                ByBank = measurement.GroupBy(r => r.Bank).Select(b => new
                {
                    Bank = b.Key,
                    Scores = Enumerable.Range(0, SsaAdvisory.Horizon).Select(h => methods.Select((m, n) => new
                    {
                        Method = m, Overall = Score(b, h, n, Predict), Drain = Score(b.Where(r => r.State.Active && InWindow(r, r.Drain)), h, n, Predict),
                    })),
                }).ToArray(),
            };

            Dictionary<string, double[][]> Paths(LongitudinalSample sample) => sample.Banks.ToDictionary(b => b.SubjectId, b =>
            {
                var inputs = Residuals(b, threshold, medians);
                var replay = RevenantSsaInference.ForecastCumulativeResiduals(ml, saved, spec, inputs);
                // Reopen the exact same immutable artifact and score a short causal prefix.
                const int check = 12;
                var prefix = RevenantSsaInference.ForecastCumulativeResiduals(ml, saved, spec, inputs[..(spec.Warmup + check)]);
                Assert.Equal(check, prefix.Length);
                for (var k = 0; k < prefix.Length; k++) Assert.Equal(replay[k], prefix[k]);
                return replay;
            });
            long Baseline(DrainRow r, int h) => MedianPrediction(r, h, medians);
            double Movement(Dictionary<string, double[][]> replay, DrainRow r, int h) =>
                SsaAdvisory.Scale(r.Opening, ResidualScaleDivisor) * replay[r.Bank][r.Bucket - CoreOrigin][h];
            long Predict(DrainRow r, int m, int h) => m switch
            {
                0 => r.Current,
                1 => R7Evaluation.TrailingMedian(levels[r.Bank], r.Bucket),
                2 => checked(r.Current + checked(levels[r.Bank][r.Bucket + h + 1 - DailyBuckets] - levels[r.Bank][r.Bucket - DailyBuckets])),
                3 => Baseline(r, h),
                4 => SsaTrailingRate.AddMovement(Baseline(r, h), Correction(measureBanks, r, h, false) * recentWeights[r.State.Active ? 1 : 0][h]),
                5 => SsaTrailingRate.AddMovement(Baseline(r, h), Correction(measureBanks, r, h, true) * seasonalWeights[r.State.Active ? 1 : 0][h]),
                6 => SsaTrailingRate.AddMovement(Baseline(r, h), Movement(paths, r, h) * weights[r.State.Active ? 1 : 0][h]),
                _ => throw new ArgumentOutOfRangeException(nameof(m)),
            };
        }).ToArray();
        var passing = rotations.Count(r => r.Gate.Pass);
        var stableHorizons = Enumerable.Range(0, SsaAdvisory.Horizon).Where(h =>
            rotations.Count(r => r.Gate.Pass && r.Parameters.Weights[1][h] > 0) >= 2).Select(h => h + 1).ToArray();
        var pass = passing >= 2 && stableHorizons.Length >= 2;
        R7RetainedPilotDiagnosticsTests.Write(output, "e4-frozen-residual-ssa.json", new
        {
            ManifestSha256 = manifest, TrainingManifestSha256 = trainingManifestHash, Revision = revision, Platform = RuntimeInformation.RuntimeIdentifier,
            MLNet = "5.0.1", Runtime = RuntimeInformation.FrameworkDescription, Rotations = rotations, PassingRotations = passing,
            StableActiveHorizons = stableHorizons, Pass = pass, Decision = pass ? "GENUINE INCREMENTAL SSA SKILL"
                : "SSA RESIDUAL FORECAST CONTRIBUTION NOT DEMONSTRATED",
        });
    }

    private static double[][] LearnContinuousWeights(DrainRow[] calibration, Func<DrainRow, int, long> baseline,
        Func<DrainRow, int, double> correction) =>
        new[] { false, true }.Select(active => Enumerable.Range(0, SsaAdvisory.Horizon).Select(h =>
        {
            var ratios = calibration.Where(r => r.State.Active == active && r.Future[h].HasValue)
                .Select(r => (Error: (double)r.Future[h]!.Value - baseline(r, h), Correction: correction(r, h)))
                .Where(p => p.Correction != 0).Select(p => (Ratio: p.Error / p.Correction, Mass: Math.Abs(p.Correction)))
                .OrderBy(p => p.Ratio).ToArray();
            double mass = 0;
            var half = ratios.Sum(p => p.Mass) / 2;
            foreach (var p in ratios)
            {
                mass += p.Mass;
                if (mass >= half) return Math.Clamp(p.Ratio, 0, 1);
            }
            return 0d;
        }).ToArray()).ToArray();

    private sealed record Association(int Count, double? Correlation);

    private static Association Pearson(IEnumerable<(double X, double Y)> points)
    {
        var data = points.ToArray();
        if (data.Length < 2) return new(data.Length, null);
        var mx = data.Average(p => p.X);
        var my = data.Average(p => p.Y);
        var denominator = Math.Sqrt(data.Sum(p => Math.Pow(p.X - mx, 2)) * data.Sum(p => Math.Pow(p.Y - my, 2)));
        return new(data.Length, denominator == 0 ? null : data.Sum(p => (p.X - mx) * (p.Y - my)) / denominator);
    }
}
