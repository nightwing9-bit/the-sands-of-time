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
    private const int ContinuationWindow = 4;
    private static readonly string[] D0Methods = ["Persistence", "SignedDrift", "GatedDrift", "ConditionalMedian"];
    private static readonly string[] D1Methods = ["Persistence", "UngatedSsa", "GatedSsa", "GatedDrift", "ConditionalMedian"];

    private sealed record DrainState(int NegativeBuckets, int MaterialDebits, long Net, long LargestDebit,
        double NegativeFraction, int TrailingOutflowBuckets, long AboveFloor)
    {
        internal bool Active => MaterialDebits >= 2 && Net < 0;
    }

    private sealed record DrainRow(string Bank, int Bucket, long Current, long Opening, DrainState State, long?[] Future,
        long[] Drift, bool Isolated, LongitudinalEvent[] Drain, LongitudinalEvent[] Recovery, LongitudinalEvent[] Shock,
        LongitudinalEvent[] Stress, V4FutureFlow?[] Flows);

    [Fact]
    [Trait("Category", "R7Diagnostics")]
    public void Audit_observable_G4_drain_continuation()
    {
        var (roots, output, manifest) = DiagnosticInput(readFlows: true);
        var rotations = Enumerable.Range(0, 3).Select(i =>
        {
            var train = roots[i];
            var measure = roots[(i + 2) % 3];
            var threshold = Materiality(train);
            var training = DrainRows(train, threshold);
            var evaluation = DrainRows(measure, threshold);
            var medians = FitMedian(training);
            var scores = ReportScores(evaluation, D0Methods, Predict);
            var episodeAudit = AuditEpisodes(measure, threshold, evaluation);
            return new
            {
                Train = train.Manifest.Root.Key, Calibration = roots[(i + 1) % 3].Manifest.Root.Key,
                Measurement = measure.Manifest.Root.Key, Materiality = threshold, MedianFractions = medians,
                TrainingStates = new { Active = training.Count(r => r.State.Active), Drain = training.Count(r => r.State.Active && InWindow(r, r.Drain)) },
                MeasurementStates = CountStates(evaluation), Episodes = episodeAudit,
                Overall = scores, ByBank = evaluation.GroupBy(r => r.Bank).Select(b => new { Bank = b.Key, Scores = ReportScores(b.ToArray(), D0Methods, Predict) }),
                Features = evaluation.Where(r => r.State.Active).Select(r => new { r.Bank, r.Bucket, r.State }),
            };

            long Predict(DrainRow row, int method, int h) => method switch
            {
                0 => row.Current,
                1 => row.Drift[h],
                2 => row.State.Active ? row.Drift[h] : row.Current,
                3 => MedianPrediction(row, h, medians),
                _ => throw new ArgumentOutOfRangeException(nameof(method)),
            };
        }).ToArray();
        R7RetainedPilotDiagnosticsTests.Write(output, "d0-continuation.json", new { ManifestSha256 = manifest, Rotations = rotations });
    }

    [Fact]
    [Trait("Category", "R7DiagnosticsNative")]
    public void Measure_gated_SSA_on_executed_G4()
    {
        Assert.True(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64);
        var (roots, output, manifest) = DiagnosticInput();
        var evidence = Environment.GetEnvironmentVariable("REVENANT_R7_CORE_EVIDENCE")
            ?? throw new InvalidOperationException("Set REVENANT_R7_CORE_EVIDENCE to the retained Gate D run.");
        var ml = new MLContext(seed: 7);
        var rotations = ReadExistingRotations(evidence, manifest);
        var result = Enumerable.Range(0, 3).Select(i =>
        {
            var prior = rotations[i];
            var spec = new SsaForecastSpec(SsaForecastRepresentation.CompletedIntervalChange, 24, 96, 2005, null, CoreWarmup, 1_000);
            var zip = File.ReadAllBytes(Path.Combine(evidence, "research-zips", $"train-{i}", "trailing-rate.zip"));
            ObservationEncoding.RequireDigest(zip, prior.GetProperty("ArtifactSha256").GetString()!);
            var oldEdges = prior.GetProperty("Edges").Deserialize<double[]>()!;
            var oldWeights = prior.GetProperty("Weights").Deserialize<double[][][]>()![ExactIndex];
            return GatedRotation(roots[i], roots[(i + 1) % 3], roots[(i + 2) % 3],
                (root, bank) => RevenantSsaInference.ForecastTrailingRate(ml, zip, spec, bank.ReferenceMinor!.Value, Values(bank))
                    .ToDictionary(s => s.Bucket, s =>
                    {
                        var opening = bank.ReferenceMinor!.Value;
                        var scale = SsaAdvisory.Scale(opening, spec.ScaleDivisor);
                        var sigma = s.Sigma / (double)scale;
                        var group = sigma <= oldEdges[0] ? 0 : sigma <= oldEdges[1] ? 1 : 2;
                        var balance = bank.Points[s.Bucket].Value;
                        return new ForecastPaths(s.ExactInverse,
                            s.ExactInverse.Select((level, h) => SsaTrailingRate.Shrink(balance, level, oldWeights[group][h])).ToArray());
                    }), ObservationEncoding.Hash(zip), prior.GetProperty("TrainingBank").GetString()!);
        }).ToArray();
        R7RetainedPilotDiagnosticsTests.Write(output, "d1-gated-ssa.json", new { ManifestSha256 = manifest, Rotations = result });
    }

    [Fact]
    public void Three_bucket_oracle_uses_predicted_anchors_beyond_three_horizons()
    {
        const int origin = 6;
        long[] balances = [1000, 1000, 1000, 1000, 1000, 1000, 880, 760, 760, 1000, 1000, 1000, 880, 880, 880];
        var rates = Enumerable.Range(origin + 1, 8).Select(i => SsaTrailingRate.Input(balances, 1000, 1, i, 3)).ToArray();
        Assert.Equal(balances[(origin + 1)..], SsaTrailingRate.Decode(SsaTrailingRateDecoder.TrailingRateExactInverse,
            balances[..(origin + 1)], 1, origin, rates, 3));
    }

    [Fact]
    [Trait("Category", "R7DiagnosticsNative")]
    public void Measure_three_bucket_flow_SSA_on_executed_G4()
    {
        Assert.True(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64);
        var (roots, output, manifest) = DiagnosticInput();
        var evidence = Environment.GetEnvironmentVariable("REVENANT_R7_CORE_EVIDENCE")
            ?? throw new InvalidOperationException("Set REVENANT_R7_CORE_EVIDENCE to the retained Gate D run.");
        var prior = ReadExistingRotations(evidence, manifest);
        var ml = new MLContext(seed: 7);
        const int window = 3;
        var rotations = Enumerable.Range(0, 3).Select(i =>
        {
            var train = roots[i];
            var trainingBank = prior[i].GetProperty("TrainingBank").GetString()!;
            var bank = train.Banks.Single(b => b.SubjectId == trainingBank);
            var spec = R7QualificationEvaluationTests.ForecastCandidates[0].Spec with { TrainSize = bank.Points.Length - window + 1 };
            var zip = RevenantSsaInference.FitTrailingRate(ml, spec, bank.ReferenceMinor!.Value, Values(bank), window);
            File.WriteAllBytes(Path.Combine(output, $"d2-root-{i}.zip"), zip);
            var (drain, recovery, shock) = StressWindows(train, trainingBank);
            return new
            {
                TrainingSeriesSha256 = bank.Digest, Spec = spec,
                TrainingCoverage = new
                {
                    Seals = bank.Points.Length, NonzeroChanges = Values(bank).Zip(Values(bank).Skip(1)).Count(x => x.First != x.Second),
                    Drain = drain.Select(e => new { e.Start, e.End, Minimum = bank.Points.Skip(e.Start).Take(e.End - e.Start + 1).Min(p => p.Value) }),
                    Recovery = recovery.Length, Shock = shock.Length,
                },
                Results = GatedRotation(train, roots[(i + 1) % 3], roots[(i + 2) % 3], (_, subject) =>
                {
                    var opening = subject.ReferenceMinor!.Value;
                    var levels = Values(subject);
                    var forecasts = RevenantSsaInference.ForecastTrailingRate(ml, zip, spec, opening, levels, window);
                    var prefix = RevenantSsaInference.ForecastTrailingRate(ml, zip, spec, opening, levels[..(CoreOrigin + 1)], window)[^1];
                    Assert.Equal(forecasts.Single(s => s.Bucket == CoreOrigin).ExactInverse, prefix.ExactInverse);
                    var altered = levels[..(CoreOrigin + 1)];
                    altered[0] = checked(altered[0] + 123_457);
                    Assert.Equal(prefix.Rates, RevenantSsaInference.ForecastTrailingRate(ml, zip, spec, opening, altered, window)[^1].Rates);
                    return forecasts.Where(s => s.Bucket >= CoreOrigin)
                        .ToDictionary(s => s.Bucket, s => new ForecastPaths(s.ExactInverse, s.ExactInverse));
                }, ObservationEncoding.Hash(zip), trainingBank),
            };
        }).ToArray();
        R7RetainedPilotDiagnosticsTests.Write(output, "d2-three-bucket-ssa.json", new
        {
            ManifestSha256 = manifest, Representation = "three-completed-interval-net-flow/1", Runtime = RuntimeInformation.FrameworkDescription,
            Platform = RuntimeInformation.RuntimeIdentifier, Rotations = rotations,
        });
    }

    private sealed record ForecastPaths(long[] Raw, long[] Ungated);

    private static JsonElement[] ReadExistingRotations(string evidence, string manifest)
    {
        using var report = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(evidence, "gate-d-rotations.json")));
        Assert.Equal(manifest, report.RootElement.GetProperty("ManifestSha256").GetString());
        var rotations = report.RootElement.GetProperty("Rotations").EnumerateArray().Select(r => r.Clone()).ToArray();
        Assert.Equal(new[] { 0, 1, 2 }, rotations.Select(r => r.GetProperty("Train").GetInt32()));
        return rotations;
    }

    private static object GatedRotation(LongitudinalSample train, LongitudinalSample calibrate, LongitudinalSample measure,
        Func<LongitudinalSample, V4AdvisorySeries, Dictionary<int, ForecastPaths>> predictBank, string artifact, string trainingBank)
    {
        var threshold = Materiality(train);
        var medians = FitMedian(DrainRows(train, threshold));
        var calibration = DrainRows(calibrate, threshold);
        var eval = DrainRows(measure, threshold);
        var calibrationPaths = Paths(calibrate);
        var evalPaths = Paths(measure);
        var ssaWeights = Weights((r, h) => calibrationPaths[r.Bank][r.Bucket].Raw[h]);
        var driftWeights = Weights((r, h) => r.Drift[h]);
        string[] methods = [.. D1Methods, "DriftWithSsaWeights", "TrailingMedian", "SeasonalNaive"];
        var levels = measure.Banks.ToDictionary(b => b.SubjectId, Values);
        var overall = ReportScores(eval, methods, Predict);
        var episodeScores = eval.GroupBy(r => r.Bank).SelectMany(group => group.First().Drain.Select(episode =>
        {
            var bank = group.Key;
            var opening = group.First().Opening;
            var minimum = levels[bank][episode.Start..(episode.End + 1)].Min();
            var eligible = group.Where(r => r.State.Active && r.Bucket >= episode.Start && r.Bucket < episode.End).ToArray();
            return new
            {
                Bank = bank, episode.Start, episode.End, Minimum = minimum,
                ReachedBelowFloor = minimum < opening / 10,
                NearMiss = minimum >= opening / 10 && minimum <= opening * 13 / 100,
                EligibleOrigins = eligible.Length,
                Scores = ReportScores(eligible, methods, Predict),
            };
        }));
        return new
        {
            Train = train.Manifest.Root.Key, Calibration = calibrate.Manifest.Root.Key, Measurement = measure.Manifest.Root.Key,
            TrainingBank = trainingBank, Artifact = artifact, Materiality = threshold, SsaWeights = ssaWeights, DriftWeights = driftWeights,
            MeasurementStates = CountStates(eval), CalibrationStates = CountStates(calibration), Overall = overall,
            ByBank = eval.GroupBy(r => r.Bank).Select(g => new { Bank = g.Key, Scores = ReportScores(g.ToArray(), methods, Predict) }),
            ByDevelopingEpisode = episodeScores,
            ChangedEligibleTargets = eval.Where(r => r.State.Active).Sum(r => Enumerable.Range(0, SsaAdvisory.Horizon)
                .Count(h => r.Future[h].HasValue && Predict(r, 2, h) != r.Current)),
            FalseNegativeMovementPredictions = new[] { 1, 2, 3 }.Select(m => new
            {
                Method = methods[m], ByHorizon = Enumerable.Range(0, SsaAdvisory.Horizon).Select(h => eval.Count(r => r.State.Active
                    && !InWindow(r, r.Drain) && r.Future[h] >= r.Current && Predict(r, m, h) < r.Current)),
            }),
        };

        Dictionary<string, Dictionary<int, ForecastPaths>> Paths(LongitudinalSample root) =>
            root.Banks.ToDictionary(b => b.SubjectId, b => predictBank(root, b));

        double[] Weights(Func<DrainRow, int, long> raw) => Enumerable.Range(0, SsaAdvisory.Horizon).Select(h =>
            SsaTrailingRate.FitWeight(calibration.Where(r => r.State.Active && r.Future[h].HasValue)
                .Select(r => (checked(r.Future[h]!.Value - r.Current), checked(raw(r, h) - r.Current))))).ToArray();

        long Predict(DrainRow r, int m, int h) => m switch
        {
            0 => r.Current,
            1 => evalPaths[r.Bank][r.Bucket].Ungated[h],
            2 => r.State.Active ? SsaTrailingRate.Shrink(r.Current, evalPaths[r.Bank][r.Bucket].Raw[h], ssaWeights[h]) : r.Current,
            3 => r.State.Active ? SsaTrailingRate.Shrink(r.Current, r.Drift[h], driftWeights[h]) : r.Current,
            4 => MedianPrediction(r, h, medians),
            5 => r.State.Active ? SsaTrailingRate.Shrink(r.Current, r.Drift[h], ssaWeights[h]) : r.Current,
            6 => R7Evaluation.TrailingMedian(levels[r.Bank], r.Bucket),
            7 => checked(r.Current + checked(levels[r.Bank][r.Bucket + h + 1 - DailyBuckets] - levels[r.Bank][r.Bucket - DailyBuckets])),
            _ => throw new ArgumentOutOfRangeException(nameof(m)),
        };
    }

    private static double Materiality(LongitudinalSample root)
    {
        var ordinaryDebits = root.Banks.SelectMany(bank =>
        {
            var levels = Values(bank);
            var (drain, recovery, shock) = StressWindows(root, bank.SubjectId);
            var exclusions = root.Events.Where(e => e.Subject == bank.SubjectId)
                .Concat(drain).Concat(recovery).Concat(shock);
            var opening = bank.ReferenceMinor!.Value;
            return Enumerable.Range(1, levels.Length - 1)
                .Where(i => levels[i] < levels[i - 1] && !exclusions.Any(e => i >= e.Start && i <= e.End))
                .Select(i => (levels[i - 1] - levels[i]) / (double)opening);
        }).Order().ToArray();
        return ordinaryDebits.Length == 0 ? double.PositiveInfinity : ordinaryDebits[(int)Math.Ceiling(.95 * ordinaryDebits.Length) - 1];
    }

    private static DrainState Observe(long[] levels, long opening, int t, double threshold)
    {
        var changes = Enumerable.Range(t - ContinuationWindow + 1, ContinuationWindow).Select(i => checked(levels[i] - levels[i - 1])).ToArray();
        var debits = changes.Where(x => x < 0).Select(x => checked(-x)).ToArray();
        return new(debits.Length, debits.Count(x => x / (double)opening >= threshold), checked(levels[t] - levels[t - ContinuationWindow]),
            debits.DefaultIfEmpty().Max(), debits.Sum() / (double)opening, changes.Reverse().TakeWhile(x => x < 0).Count(),
            checked(levels[t] - opening / 10));
    }

    private static DrainRow[] DrainRows(LongitudinalSample root, double threshold, int firstOrigin = CoreOrigin) => root.Banks.SelectMany(bank =>
    {
        var levels = Values(bank);
        var opening = bank.ReferenceMinor!.Value;
        var scale = SsaAdvisory.Scale(opening, 1_000);
        var (drain, recovery, shock) = StressWindows(root, bank.SubjectId);
        var stress = root.Events.Where(e => e.Subject == bank.SubjectId).ToArray();
        return Enumerable.Range(firstOrigin, levels.Length - firstOrigin).Select(t =>
        {
            var state = Observe(levels, opening, t, threshold);
            var rate = SsaTrailingRate.Input(levels, opening, scale, t);
            var drift = SsaTrailingRate.Decode(SsaTrailingRateDecoder.TrailingRateIntegratedExtrapolation, levels, scale, t,
                Enumerable.Repeat(rate, SsaAdvisory.Horizon).ToArray());
            var isolated = state.MaterialDebits == 1 && (levels[t - 1] - levels[t]) / (double)opening >= threshold
                && !stress.Any(e => t >= e.Start && t <= e.End);
            return new DrainRow(bank.SubjectId, t, levels[t], opening, state, root.Targets[bank.SubjectId][t], drift,
                isolated, drain, recovery, shock, stress, root.Flows?[bank.SubjectId] ?? []);
        });
    }).ToArray();

    private static double[][] FitMedian(DrainRow[] training) => new[] { false, true }.Select(active =>
        Enumerable.Range(0, SsaAdvisory.Horizon).Select(h => Median(training.Where(r => r.State.Active == active && r.Future[h].HasValue)
            .Select(r => (r.Future[h]!.Value - r.Current) / (double)r.Opening)) ?? 0).ToArray()).ToArray();

    private static long MedianPrediction(DrainRow row, int h, double[][] medians) =>
        SsaTrailingRate.AddMovement(row.Current, row.Opening * medians[row.State.Active ? 1 : 0][h]);

    private static double? Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? null : .5 * sorted[(sorted.Length - 1) / 2] + .5 * sorted[sorted.Length / 2];
    }

    private static bool InWindow(DrainRow row, LongitudinalEvent[] windows) =>
        windows.Any(e => row.Bucket >= e.Start && row.Bucket < e.End);

    private static object CountStates(DrainRow[] rows) => new
    {
        Origins = rows.Length,
        Activated = rows.Count(r => r.State.Active),
        ActualDrain = rows.Count(r => r.State.Active && InWindow(r, r.Drain)),
        Recovery = rows.Count(r => r.State.Active && InWindow(r, r.Recovery)),
        Ordinary = rows.Count(r => r.State.Active && !InWindow(r, r.Stress)),
        Isolated = rows.Count(r => r.Isolated),
        Consecutive = rows.Count(r => r.State.TrailingOutflowBuckets >= 2),
    };

    private static object[] AuditEpisodes(LongitudinalSample root, double threshold, DrainRow[] evaluation) =>
        root.Banks.SelectMany(bank =>
        {
            var levels = Values(bank);
            var opening = bank.ReferenceMinor!.Value;
            var (drains, _, _) = StressWindows(root, bank.SubjectId);
            var mask = evaluation.Where(r => r.Bank == bank.SubjectId).Select(r => r.Bucket).ToHashSet();
            return drains.Select(d =>
            {
                var origin = Enumerable.Range(d.Start, d.End - d.Start + 1).ToArray();
                var firstDebit = origin.Where(i => levels[i] < levels[i - 1]).Select(i => (int?)i).FirstOrDefault();
                var recognition = origin.Where(i => Observe(levels, opening, i, threshold).Active).Select(i => (int?)i).FirstOrDefault();
                var crossing = origin.Where(i => levels[i - 1] >= opening / 10 && levels[i] < opening / 10)
                    .Select(i => (int?)i).FirstOrDefault();
                return (object)new
                {
                    Bank = bank.SubjectId, d.Start, d.End, FirstDebit = firstDebit, Recognition = recognition, Crossing = crossing,
                    LeadMinutes = 5 * (crossing - recognition), EligibleAtRecognition = recognition.HasValue && mask.Contains(recognition.Value),
                    ActiveOriginsBeforeCrossing = origin.Count(i => i < crossing && Observe(levels, opening, i, threshold).Active),
                    MinimumFraction = origin.Min(i => levels[i]) / (double)opening,
                    LastObservedDebit = origin.Where(i => levels[i] < levels[i - 1]).Select(i => (int?)i).LastOrDefault(),
                };
            });
        }).ToArray();

    private sealed record DrainScores(int Targets, double? Zero, double? Positive, double? Negative,
        double? MedianMovement, double? MeanMovement, double? MeanSign, double? MedianSign, double? Mae, double? Bias,
        double? SignAccuracy, int? OrdinarySettlementOnly, int? Developing, int? ReturnsOrRecovery, int? MixedOrOther);

    private static object[] ReportScores(DrainRow[] rows, string[] methods, Func<DrainRow, int, int, long> predict) =>
        Enumerable.Range(0, SsaAdvisory.Horizon).Select(h => (object)new
        {
            Minutes = (h + 1) * 5,
            Metrics = methods.Select((method, m) => new
            {
                Method = method,
                Overall = Score(rows, h, m, predict),
                Continuation = Score(rows.Where(r => r.State.Active), h, m, predict),
                Ordinary = Score(rows.Where(r => !OverlapsWindow(r, h, r.Stress)), h, m, predict),
                ContinuingDrain = Score(rows.Where(r => r.State.Active && InWindow(r, r.Drain)), h, m, predict),
                Developing = Score(rows.Where(r => OverlapsWindow(r, h, r.Drain)), h, m, predict),
                Recovery = Score(rows.Where(r => OverlapsWindow(r, h, r.Recovery)), h, m, predict),
                Shock = Score(rows.Where(r => OverlapsWindow(r, h, r.Shock)), h, m, predict),
                Isolated = Score(rows.Where(r => r.Isolated), h, m, predict),
                ConsecutiveDebits = Score(rows.Where(r => r.State.TrailingOutflowBuckets >= 2), h, m, predict),
                OutcomeActive = Score(rows.Where(r => r.Future[h] is { } y && Math.Abs((double)y - r.Current) >= R7ForecastMetrics.ActiveChangeMinor
                    || OverlapsWindow(r, h, r.Stress)), h, m, predict),
            }).ToArray(),
            DrainEndsBeforeHorizon = rows.Count(r => r.State.Active && r.Future[h].HasValue
                && r.Drain.Any(e => r.Bucket >= e.Start && r.Bucket < e.End && r.Bucket + h + 1 > e.End)),
            DrainContinuationTargets = rows.Count(r => r.State.Active && r.Future[h].HasValue && InWindow(r, r.Drain)),
        }).ToArray();

    private static bool OverlapsWindow(DrainRow row, int h, LongitudinalEvent[] windows) =>
        windows.Any(e => row.Bucket + h + 1 >= e.Start && row.Bucket <= e.End);

    private static DrainScores Score(IEnumerable<DrainRow> rows, int h, int method, Func<DrainRow, int, int, long> predict)
    {
        var samples = rows.Where(r => r.Future[h].HasValue).Select(r => (Row: r, Actual: checked(r.Future[h]!.Value - r.Current),
            Predicted: checked(predict(r, method, h) - r.Current))).ToArray();
        if (samples.Length == 0) return new(0, null, null, null, null, null, null, null, null, null, null, null, null, null, null);
        var n = samples.Length;
        int? settlement = null, developing = null, recovery = null, other = null;
        if (method == 0 && samples[0].Row.Flows.Length > 0)
        {
            var nonzero = samples.Where(s => s.Actual != 0).ToArray();
            var pressure = nonzero.Count(s => OverlapsWindow(s.Row, h, s.Row.Drain));
            var returns = nonzero.Count(s => !OverlapsWindow(s.Row, h, s.Row.Drain)
                && (OverlapsWindow(s.Row, h, s.Row.Recovery) || Enumerable.Range(s.Row.Bucket + 1, h + 1)
                    .Any(i => s.Row.Flows[i] is { Returns: > 0 })));
            var settle = nonzero.Count(s => !OverlapsWindow(s.Row, h, s.Row.Stress)
                && Enumerable.Range(s.Row.Bucket + 1, h + 1).All(i => s.Row.Flows[i] is { Returns: 0 })
                && Enumerable.Range(s.Row.Bucket + 1, h + 1).Sum(i => s.Row.Flows[i]!.NetSettled) == s.Actual
                && s.Row.Flows[s.Row.Bucket]!.Balance.Reserved == s.Row.Flows[s.Row.Bucket + h + 1]!.Balance.Reserved);
            (settlement, developing, recovery, other) = (settle, pressure, returns, nonzero.Length - settle - pressure - returns);
        }
        return new(n, samples.Count(s => s.Actual == 0) / (double)n, samples.Count(s => s.Actual > 0) / (double)n,
            samples.Count(s => s.Actual < 0) / (double)n, Median(samples.Select(s => (double)s.Actual)),
            samples.Average(s => (double)s.Actual), samples.Average(s => (double)Math.Sign(s.Actual)),
            Median(samples.Select(s => (double)Math.Sign(s.Actual))), samples.Average(s => Math.Abs((double)s.Predicted - s.Actual)),
            samples.Average(s => (double)s.Predicted - s.Actual),
            samples.Count(s => Math.Sign(s.Predicted) == Math.Sign(s.Actual)) / (double)n,
            settlement, developing, recovery, other);
    }
}
