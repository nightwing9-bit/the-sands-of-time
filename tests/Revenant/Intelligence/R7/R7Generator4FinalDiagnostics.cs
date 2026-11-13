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
    private const int OwnTraining = 576;

    [Fact]
    [Trait("Category", "R7Diagnostics")]
    public void Measure_G4_own_bank_and_transfer_AR_control()
    {
        var (roots, output, manifest) = DiagnosticInput();
        R7RetainedPilotDiagnosticsTests.Write(output, "g4-final-t1-t2-ar.json", new
        {
            ManifestSha256 = manifest,
            Results = MeasureTransfer(roots, null),
        });
    }

    [Fact]
    [Trait("Category", "R7DiagnosticsNative")]
    public void Measure_G4_SSA_transfer_and_warning_diagnostics()
    {
        Assert.True(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64);
        var (roots, output, manifest) = DiagnosticInput();
        var ml = new MLContext(seed: 7);
        var transfer = MeasureTransfer(roots, ml);
        R7RetainedPilotDiagnosticsTests.Write(output, "g4-final-t1-t2-ssa.json", new
        {
            ManifestSha256 = manifest,
            Runtime = RuntimeInformation.FrameworkDescription,
            Platform = RuntimeInformation.RuntimeIdentifier,
            Results = transfer,
        });
        R7RetainedPilotDiagnosticsTests.Write(output, "g4-final-t3-warnings.json", new
        {
            ManifestSha256 = manifest,
            Results = MeasureWarnings(ml, roots),
        });
    }

    private static (LongitudinalSample[] Roots, string Output, string Manifest) DiagnosticInput()
    {
        var input = Environment.GetEnvironmentVariable("REVENANT_R7_PILOT_INPUT")
            ?? throw new InvalidOperationException("Set REVENANT_R7_PILOT_INPUT to the retained G4 pilot.");
        var output = Environment.GetEnvironmentVariable("REVENANT_R7_DIAGNOSTICS_OUTPUT")
            ?? throw new InvalidOperationException("Set REVENANT_R7_DIAGNOSTICS_OUTPUT.");
        Directory.CreateDirectory(output);
        return (ReadExecutedPilot(input), output, ObservationEncoding.Hash(File.ReadAllBytes(Path.Combine(input, "pilot.json"))));
    }

    private static object MeasureTransfer(LongitudinalSample[] roots, MLContext? ml)
    {
        var spec = R7QualificationEvaluationTests.ForecastCandidates[0].Spec with { TrainSize = OwnTraining };
        var reference = roots[0].Banks.Single(b => b.SubjectId == "BANK-B");
        var referenceLevels = Values(reference);
        var referencePeriods = Periodogram(Changes(reference, spec)[..OwnTraining]);
        var matched = ml is null ? null : RevenantSsaInference.FitForecast(ml, spec, reference.ReferenceMinor!.Value, referenceLevels, 0);
        var historicalSpec = R7QualificationEvaluationTests.ForecastCandidates[0].Spec;
        var historical = ml is null ? null : RevenantSsaInference.FitForecast(ml, historicalSpec, reference.ReferenceMinor!.Value, referenceLevels, 0);
        var arReference = ml is null ? R7Diagnostics.FitAutoregression([Changes(reference, spec)[..OwnTraining]], spec.Window, 1) : null;
        var target = roots[2];
        var localCases = new List<R7ForecastMetrics.BankCase>();
        var transferCases = new List<R7ForecastMetrics.BankCase>();
        var banks = target.Banks.Select(bank =>
        {
            var levels = Values(bank);
            var changes = Changes(bank, spec);
            var periods = Periodogram(changes[..OwnTraining]);
            var artifact = ml is null ? null : RevenantSsaInference.FitForecast(ml, spec, bank.ReferenceMinor!.Value, levels, 0);
            var local = ml is null
                ? ArCase(target, bank, changes, R7Diagnostics.FitAutoregression([changes[..OwnTraining]], spec.Window, 1), spec)
                : SsaCase(ml, target, bank, artifact!, spec, OwnTraining);
            var transferred = ml is null ? ArCase(target, bank, changes, arReference!, spec)
                : SsaCase(ml, target, bank, matched!, spec);
            var localMetrics = R7ForecastMetrics.Evaluate([local]);
            var transferredMetrics = R7ForecastMetrics.Evaluate([transferred]);
            localCases.Add(local);
            transferCases.Add(transferred);
            var nearCycle = CycleOrigins(bank, changes, periods[0].Period);
            return new
            {
                Bank = bank.SubjectId, TrainingPrefixSha256 = PrefixHash(bank), ArtifactSha256 = Digest(artifact),
                Peaks = periods, DominantPeriodMatches = Math.Abs(periods[0].Period - referencePeriods[0].Period) <= .5,
                Local = Report(local, localMetrics), Transferred = Report(transferred, transferredMetrics),
                LocalRelativeImprovement = Improvement(localMetrics, transferredMetrics),
                NearCycle = new
                {
                    Origins = nearCycle.Count,
                    Local = R7ForecastMetrics.Evaluate([local with { Origins = [.. local.Origins.Where(o => nearCycle.Contains(o.Bucket))] }]),
                    Transfer = R7ForecastMetrics.Evaluate([transferred with { Origins = [.. transferred.Origins.Where(o => nearCycle.Contains(o.Bucket))] }]),
                },
                HistoricalFullLength = ml is null ? null : Report(SsaCase(ml, target, bank, historical!, historicalSpec)),
            };
        }).ToArray();
        var pooledLocal = R7ForecastMetrics.Evaluate([.. localCases]);
        var pooledTransfer = R7ForecastMetrics.Evaluate([.. transferCases]);
        var ratios = banks.Select(b => b.Local.OverallRatio).Order().ToArray();
        var median = (ratios[2] + ratios[3]) / 2;
        var skill = R7QualificationEvaluationTests.Skill(pooledLocal);
        var improvement = Improvement(pooledLocal, pooledTransfer);
        return new
        {
            TrainPoints = OwnTraining, FirstMeasuredOrigin = OwnTraining, Spec = spec,
            SourceRoot = roots[0].Manifest.Root.Key, MeasurementRoot = target.Manifest.Root.Key,
            TransferBank = reference.SubjectId, TransferTrainingSha256 = PrefixHash(reference),
            TransferArtifactSha256 = Digest(matched), HistoricalTransferArtifactSha256 = Digest(historical),
            HistoricalTransferTrainingSha256 = reference.Digest,
            TransferPeaks = referencePeriods, Banks = banks,
            MedianOwnRatio = median, PooledOwnActiveSkill = skill,
            PooledOwnRatio = R7QualificationEvaluationTests.OverallRatio(pooledLocal),
            PooledTransferRatio = R7QualificationEvaluationTests.OverallRatio(pooledTransfer),
            PooledImprovement = improvement,
            T1Pass = median <= .95 && skill >= .1,
            T2ThresholdMet = improvement >= .25 && banks.Any(b => !b.DominantPeriodMatches),
        };
    }

    private sealed record ForecastReport(int Origins, double OverallRatio, double ActiveSkill,
        R7ForecastMetrics.HorizonMetrics[] Horizons, R7ForecastMetrics.HorizonMetrics[] DevelopingOnly);

    private static ForecastReport Report(R7ForecastMetrics.BankCase bank, R7ForecastMetrics.HorizonMetrics[]? metrics = null)
    {
        metrics ??= R7ForecastMetrics.Evaluate([bank]);
        var developing = R7ForecastMetrics.Evaluate([bank with
        {
            Events = [.. bank.Events.Where(e => e.Kind == LongitudinalEventKind.DevelopingPressure)],
        }]);
        return new(bank.Origins.Length, R7QualificationEvaluationTests.OverallRatio(metrics),
            R7QualificationEvaluationTests.Skill(metrics), metrics, developing);
    }

    private static double Improvement(R7ForecastMetrics.HorizonMetrics[] local, R7ForecastMetrics.HorizonMetrics[] transfer) =>
        1 - local.Average(h => h.Mae!.Value) / transfer.Average(h => h.Mae!.Value);

    private static R7ForecastMetrics.BankCase SsaCase(MLContext ml, LongitudinalSample root, V4AdvisorySeries bank,
        byte[] artifact, SsaForecastSpec spec, int? fittedPrefix = null)
    {
        var steps = RevenantSsaInference.ForecastBank(ml, artifact, spec, null, bank.ReferenceMinor!.Value, Values(bank), fittedPrefix);
        return R7Evaluation.BankCase(root.Manifest.Root.Key, bank, [.. steps.Where(s => s.Bucket >= OwnTraining)],
            root.Events, root.Targets[bank.SubjectId]);
    }

    private static R7ForecastMetrics.BankCase ArCase(LongitudinalSample root, V4AdvisorySeries bank, double[] changes,
        double[] coefficients, SsaForecastSpec spec)
    {
        var levels = Values(bank);
        var opening = bank.ReferenceMinor!.Value;
        var scale = SsaAdvisory.Scale(opening, spec.ScaleDivisor);
        var steps = Enumerable.Range(OwnTraining, levels.Length - OwnTraining).Select(i =>
        {
            var delta = R7Diagnostics.PredictChanges(coefficients, changes, i, SsaAdvisory.Horizon).Select(v => (float)v).ToArray();
            var (central, lower, upper) = SsaAdvisory.DecodeLevels(spec.Representation, opening, scale, levels[i], delta, delta, delta);
            return new SsaForecastStep(i, levels[i], scale, central, lower, upper, lower, upper);
        }).ToArray();
        return R7Evaluation.BankCase(root.Manifest.Root.Key, bank, steps, root.Events, root.Targets[bank.SubjectId]);
    }

    private sealed record SpectralPeak(double Period, double RelativePower);

    private static SpectralPeak[] Periodogram(double[] changes)
    {
        var mean = changes.Average();
        var signal = changes.Select((value, i) =>
            (value - mean) * .5 * (1 - Math.Cos(2 * Math.PI * i / (changes.Length - 1)))).ToArray();
        var bins = Enumerable.Range(1, changes.Length / 2).Select(k =>
        {
            var frequency = 2 * Math.PI * k / changes.Length;
            var real = signal.Select((v, i) => v * Math.Cos(frequency * i)).Sum();
            var imaginary = signal.Select((v, i) => v * Math.Sin(frequency * i)).Sum();
            return (Period: (double)changes.Length / k, Power: real * real + imaginary * imaginary);
        }).ToArray();
        var total = bins.Sum(b => b.Power);
        return bins.Select((b, i) => (b, i)).Where(x => x.i > 0 && x.i < bins.Length - 1 && x.b.Period is >= 3 and <= 24
                && x.b.Power >= bins[x.i - 1].Power && x.b.Power >= bins[x.i + 1].Power)
            .OrderByDescending(x => x.b.Power).Take(5).Select(x => new SpectralPeak(x.b.Period, x.b.Power / total)).ToArray();
    }

    private static HashSet<int> CycleOrigins(V4AdvisorySeries bank, double[] changes, double period)
    {
        // Estimate phase from the first 576 balances; use the strongest periodic activity phase as the reference.
        var bins = Math.Max(3, (int)Math.Round(period));
        int Phase(int i) => (int)Math.Floor((i % period) / period * bins);
        var peakPhase = Enumerable.Range(0, OwnTraining).GroupBy(Phase)
            .MaxBy(g => g.Average(i => Math.Abs(changes[i])))!.Key;
        return Enumerable.Range(OwnTraining, bank.Points.Length - OwnTraining)
            .Where(i => new[] { i - 1, i, i + 1 }.Any(t => Phase(t) == peakPhase)).ToHashSet();
    }

    private static string PrefixHash(V4AdvisorySeries bank)
    {
        var prefix = bank with { Points = bank.Points[..OwnTraining], RequestedBoundaryMs = bank.Points[OwnTraining - 1].Seal.BoundaryMs };
        return prefix.Digest;
    }

    private static long[] Values(V4AdvisorySeries bank) => bank.Points.Select(p => p.Value).ToArray();
    private static double[] Changes(V4AdvisorySeries bank, SsaForecastSpec spec) =>
        R7Diagnostics.Changes(bank.ReferenceMinor!.Value, Values(bank), SsaAdvisory.Scale(bank.ReferenceMinor.Value, spec.ScaleDivisor));

    private static string? Digest(byte[]? bytes) => bytes is null ? null : ObservationEncoding.Hash(bytes);

    private static object MeasureWarnings(MLContext ml, LongitudinalSample[] roots)
    {
        var spec = R7QualificationEvaluationTests.ForecastCandidates[0].Spec;
        var bank = roots[0].Banks.Single(b => b.SubjectId == "BANK-B");
        var artifact = RevenantSsaInference.FitForecast(ml, spec, bank.ReferenceMinor!.Value, Values(bank), 0);
        var calibration = Forecast(roots[1]);
        var measurement = Forecast(roots[2]);
        var edges = calibration.SelectMany(b => b.Steps.Select(s => Volatility(b.Case, s))).Order().ToArray();
        var low = Quantile(edges, 1d / 3);
        var high = Quantile(edges, 2d / 3);
        int Group(R7ForecastMetrics.BankCase c, SsaForecastStep s) => Volatility(c, s) <= low ? 0 : Volatility(c, s) <= high ? 1 : 2;

        // Sample one origin per eight completed buckets on each bank. Adjacent sampled target windows do not overlap.
        var residuals = Enumerable.Range(0, 3).Select(g => Enumerable.Range(0, SsaAdvisory.Horizon).Select(h => calibration
            .SelectMany(b => b.Steps.Where(s => s.Bucket % SsaAdvisory.Horizon == 0 && Group(b.Case, s) == g
                && b.Case.Target(s.Bucket, h).HasValue)
                .Select(s => (b.Case.Target(s.Bucket, h)!.Value - (double)s.Central[h]) / s.Sigma))
            .Order().ToArray()).ToArray()).ToArray();
        var fallback = Enumerable.Range(0, SsaAdvisory.Horizon).Select(h => residuals.SelectMany(r => r[h]).Order().ToArray()).ToArray();
        const int minimumCalibration = 40;
        double[][] Quantiles(double probability) => residuals.Select(group => group.Select((r, h) =>
            Quantile(r.Length >= minimumCalibration ? r : fallback[h], probability)).ToArray()).ToArray();
        var lower = Quantiles(.025);
        var upper = Quantiles(.975);
        var forecastCases = measurement.Select(b => b.Case with
        {
            Origins = [.. b.Case.Origins.Select((o, i) => o with
            {
                Path = [.. o.Path.Select((p, h) => p with
                {
                    CalibratedLower = Offset(p.Central, b.Steps[i].Sigma, lower[Group(b.Case, b.Steps[i])][h], false),
                    CalibratedUpper = Offset(p.Central, b.Steps[i].Sigma, upper[Group(b.Case, b.Steps[i])][h], true),
                })],
            })],
        }).ToArray();
        var betas = new[] { .5, .25, .1 }.Select(beta =>
        {
            var q = Quantiles(beta);
            var development = Warnings(calibration, q);
            var heldOut = Warnings(measurement, q);
            return new { Beta = beta, Calibration = development, Measurement = heldOut,
                FalseWarningsPerBankDay = heldOut.FalseWarnings / (measurement.Sum(b => b.Steps.Length) / 288d) };
        }).ToArray();
        var selected = betas.Where(b => b.Calibration.Precision >= .5).OrderByDescending(b => b.Calibration.WarnedDeveloping)
            .ThenBy(b => b.Calibration.FalseWarnings).ThenByDescending(b => b.Beta).FirstOrDefault();
        return new
        {
            Fit = "Recreated root-0 BANK-B development fit from its retained 2016-point series",
            TrainingSha256 = bank.Digest, ArtifactSha256 = ObservationEncoding.Hash(artifact), VolatilityEdges = new[] { low, high },
            MinimumCalibration = minimumCalibration, CalibrationCounts = residuals.Select(g => g.Select(r => r.Length)),
            Lower = lower, Upper = upper, CentralCalibration = R7ForecastMetrics.Warnings(calibration.Select(b => b.Case).ToArray()),
            CentralMeasurement = R7ForecastMetrics.Warnings(forecastCases), CandidateQuantiles = betas, SelectedOnCalibration = selected,
            HorizonCoverage = R7ForecastMetrics.Evaluate(forecastCases),
            ByVolatility = Enumerable.Range(0, 3).Select(g => R7ForecastMetrics.Evaluate(forecastCases.Select((c, b) => c with
            {
                Origins = [.. c.Origins.Where((_, i) => Group(c, measurement[b].Steps[i]) == g)],
            }).ToArray())),
            BrierScore = "Not applicable; no calibrated joint crossing probability",
        };

        double Volatility(R7ForecastMetrics.BankCase c, SsaForecastStep s) => s.Sigma / (double)SsaAdvisory.Scale(c.Reference, spec.ScaleDivisor);

        (R7ForecastMetrics.BankCase Case, SsaForecastStep[] Steps)[] Forecast(LongitudinalSample root) =>
            R7QualificationEvaluationTests.Banks(ml, [root], artifact, spec, null);

        R7ForecastMetrics.WarningMetrics Warnings((R7ForecastMetrics.BankCase Case, SsaForecastStep[] Steps)[] banks, double[][] q)
        {
            var byBank = banks.ToDictionary(b => b.Case.Bank, b => b.Steps);
            return R7ForecastMetrics.Warnings(banks.Select(b => b.Case).ToArray(), (c, o, h) =>
            {
                var step = byBank[c.Bank][o.Bucket - spec.Warmup];
                return Offset(o.Path[h].Central, step.Sigma, q[Group(c, step)][h], false);
            });
        }
    }

    private static long Offset(long central, long sigma, double multiplier, bool upper) =>
        checked(central + (long)(upper ? Math.Ceiling(sigma * multiplier) : Math.Floor(sigma * multiplier)));

    private static double Quantile(double[] ordered, double probability) =>
        ordered[Math.Clamp((int)Math.Ceiling((ordered.Length + 1) * probability) - 1, 0, ordered.Length - 1)];
}
