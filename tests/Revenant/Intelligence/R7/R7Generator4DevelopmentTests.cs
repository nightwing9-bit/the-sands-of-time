using System.Runtime.InteropServices;
using Hosts.Worker;
using Microsoft.ML;
using Revenant.Contracts;
using Revenant.Intelligence;
using Revenant.Observations;
using Revenant.Synthetic;
using Revenant.Timeline;
using Xunit;

namespace Revenant.Tests.Intelligence.R7;

/// <summary>
/// Generator-4 development work on the three Pilot roots only (root 0 trains, root 1 selects and calibrates, root 2 measures).
/// Qualification roots are never planned here. Executed and plan-derived development measurements have separate outputs.
/// </summary>
public sealed partial class R7Generator4DevelopmentTests
{
    private const long Bucket = ObservedFinanceVersion.BucketMs;

    private static LongitudinalSample[] ReadExecutedPilot(string input)
    {
        var manifest = LongitudinalSample.ReadManifest(input, "pilot.json");
        Assert.Equal(V4LongitudinalPlannerG4.Version, manifest.Generator);
        Assert.Equal(V4LongitudinalPlannerG4.Roots(LongitudinalSplit.Pilot).Take(3), manifest.Roots.Select(r => r.Root));
        return manifest.Roots.Select(r => LongitudinalSample.Read(input, r, requirePilotTargets: true)).ToArray();
    }

    [Fact]
    [Trait("Category", "R7DiagnosticsNative")]
    public void Reconcile_and_measure_anomaly_on_generator_4_PostgreSQL_streams()
    {
        Assert.True(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64);
        var input = Environment.GetEnvironmentVariable("REVENANT_R7_PILOT_INPUT")
            ?? throw new InvalidOperationException("Set REVENANT_R7_PILOT_INPUT to the generator-4 PostgreSQL pilot.");
        var output = Environment.GetEnvironmentVariable("REVENANT_R7_DIAGNOSTICS_OUTPUT")
            ?? throw new InvalidOperationException("Set REVENANT_R7_DIAGNOSTICS_OUTPUT.");
        Directory.CreateDirectory(output);
        var roots = ReadExecutedPilot(input);
        var reconciliation = roots.Select(root =>
        {
            var planned = Planned(root.Manifest.Root);
            Assert.Equal(planned.Episode.BehaviorSha256, root.Manifest.BehaviorSha256);
            Assert.Equal(planned.Episode.Financial.Opening.Manifest.ContentHash, root.Manifest.ContentHash);
            Assert.Equal(planned.Episode.Events.ToArray(), root.Events);
            Assert.Equal(planned.Episode.Financial.Payments.Length, root.Manifest.Accepted);
            var accounts = R7Evaluation.Inputs([root]).ToDictionary(a => a.Subject, StringComparer.Ordinal);
            Assert.Equal(planned.Accounts.Length, accounts.Count);
            foreach (var account in planned.Accounts)
            {
                var executed = accounts[account.Subject];
                Assert.Equal(account.Counts, executed.Counts);
                Assert.Equal(account.Rhythm, executed.Rhythm);
            }
            Assert.Equal(root.Manifest.Accepted, accounts.Values.Sum(a => a.Counts.Sum()));
            return new
            {
                root.Manifest.Root, root.Manifest.Accepted, root.Manifest.Seals, root.Manifest.BehaviorSha256,
                root.Manifest.ContentHash, FinanceFactsThroughFinalSeal = root.Banks[0].Points[^1].Seal.FactSequence,
                FinalSealDigest = root.Banks[0].Points[^1].Seal.PrefixDigest, Accounts = accounts.Count, Banks = root.Targets.Count,
            };
        }).ToArray();

        var ml = new MLContext(seed: 7);
        var candidate = R7QualificationEvaluationTests.AnomalyCandidates[0];
        var training = R7QualificationEvaluationTests.TrainingAccount(R7Evaluation.Inputs([roots[0]]), candidate.Spec);
        var policy = candidate.Policy();
        var artifact = RevenantSsaInference.FitAnomaly(ml, candidate.Spec, training.Counts, 0);
        var calibration = Cases(roots[1]);
        var measurement = Cases(roots[2]);
        // Fixed rate uses only the pre-score prefix of each account and the same Poisson window and floor.
        var fixedCalibration = WithFixedRate(calibration);
        var fixedMeasurement = WithFixedRate(measurement);
        var budgets = R7QualificationEvaluationTests.SensitivityBudgets.Select(b => new
        {
            Budget = b,
            Ssa = Compare(calibration, measurement, R7Evaluation.Scorer.Ssa, policy, b),
            Fixed = Compare(fixedCalibration, fixedMeasurement, R7Evaluation.Scorer.Ssa, policy, b),
            Rank = Compare(calibration, measurement, R7Evaluation.Scorer.TrailingRank, null, b),
            Seasonal = Compare(calibration, measurement, R7Evaluation.Scorer.SeasonalExcess, null, b),
        }).ToArray();
        var budget = R7Evaluation.FalseAlertBudget;
        var primaryThreshold = R7Evaluation.Calibrate(calibration, R7Evaluation.Scorer.Ssa, policy,
            budget * R7QualificationEvaluationTests.CalibrationMargin).Threshold;
        var primary = R7Evaluation.Evaluate(measurement, R7Evaluation.Scorer.Ssa, policy, primaryThreshold);
        var retrospective = new[] { R7Evaluation.Scorer.TrailingRank, R7Evaluation.Scorer.SeasonalExcess }.ToDictionary(scorer => scorer,
            scorer => R7Evaluation.Calibrate(measurement, scorer, null, primary.FalseAlertsPerNormalDay ?? 0).Metrics);
        R7RetainedPilotDiagnosticsTests.Write(output, "generator-4-executed-anomaly-development.json", new
        {
            Reconciliation = reconciliation, Candidate = candidate.Name, candidate.Spec, Policy = policy,
            TrainingAccount = training.Subject, TrainingDigest = training.Digest,
            ArtifactSha256 = ObservationEncoding.Hash(artifact), Budgets = budgets,
            RetrospectiveMatchedBurden = retrospective,
            DevelopmentPassesAnomalyGate = R7QualificationEvaluationTests.PassesAnomalyGate(primary, retrospective.Values),
            PoissonAudit = measurement.Where(c => c.Steps.Length > 0).GroupBy(c => c.Rhythm).Select(group =>
            {
                var normal = group.Select(c => c.Steps.Where(s => !c.Labels.Any(e =>
                    (R7Evaluation.IsPositive(e) || R7Evaluation.IsExcluded(e)) &&
                    s.Bucket >= e.Start && s.Bucket <= e.End + R7Evaluation.LateMatch)).ToArray()).ToArray();
                var steps = normal.SelectMany(s => s).ToArray();
                var expected = steps.Sum(s => Math.Max(s.ExpectedCount, policy.Floor));
                return new
                {
                    Rhythm = group.Key, NormalBuckets = steps.Length,
                    PearsonDispersion = steps.Average(s => Math.Pow(s.Count - Math.Max(s.ExpectedCount, policy.Floor), 2)
                        / Math.Max(s.ExpectedCount, policy.Floor)),
                    ObservedToExpected = steps.Sum(s => s.Count) / expected,
                    LagOneResidualCorrelation = normal.Select(s => R7Diagnostics.Autocorrelation(
                        s.Select(p => p.Residual).ToArray(), [1])[0]).Where(v => v.HasValue).DefaultIfEmpty().Average(),
                    TailAtOnePercent = steps.Count(s => s.Statistic >= 2) / (double)steps.Length,
                };
            }).ToArray(),
        });

        R7Evaluation.AccountCase[] Cases(LongitudinalSample root) =>
            R7QualificationEvaluationTests.Accounts(ml, R7Evaluation.Inputs([root]), artifact, candidate.Spec, policy);

        R7Evaluation.AccountCase[] WithFixedRate(R7Evaluation.AccountCase[] cases) => cases.Select(c =>
        {
            var rate = c.Counts.Take(candidate.Spec.Warmup).Average();
            return c with { Steps = [.. c.Steps.Select(s => s with { ExpectedCount = rate })] };
        }).ToArray();

        static object Compare(R7Evaluation.AccountCase[] calibrate, R7Evaluation.AccountCase[] measure,
            R7Evaluation.Scorer scorer, SsaAlertPolicy? policy, double budget)
        {
            var (threshold, metrics) = R7Evaluation.Calibrate(calibrate, scorer, policy,
                budget * R7QualificationEvaluationTests.CalibrationMargin);
            return new { Threshold = threshold, Calibration = metrics, Measurement = R7Evaluation.Evaluate(measure, scorer, policy, threshold) };
        }
    }

    internal sealed record PlannedRoot(V4LongitudinalEpisode Episode, R7Evaluation.AccountInput[] Accounts,
        Dictionary<string, long[]> BankChanges);

    internal static PlannedRoot Planned(LongitudinalRoot root)
    {
        var id = new ScenarioRunId(new Guid(Convert.FromHexString(root.Key[..32]), bigEndian: true));
        var episode = V4LongitudinalPlannerG4.Plan(root, id);
        var origin = episode.Financial.Opening.Manifest.StartUtcMs;
        var buckets = root.Days * V4LongitudinalPlanner.BucketsPerDay;
        var manifest = episode.Financial.Opening.Manifest;
        var counts = manifest.Accounts.ToDictionary(a => a.AccountId, _ => new long[buckets], StringComparer.Ordinal);
        var flows = manifest.Banks.ToDictionary(b => b.BankId, _ => new long[buckets], StringComparer.Ordinal);
        foreach (var p in episode.Financial.Payments.Select(p => p.Intent))
        {
            var b = (int)((p.EventTimeUtcMs - origin) / Bucket);
            counts[p.SourceAccountId][b]++;
            if (p.SourceBankId == p.DestBankId) continue;
            flows[p.SourceBankId][b] -= p.Amount.MinorUnits;
            flows[p.DestBankId][b] += p.Amount.MinorUnits;
        }
        var inputs = manifest.Accounts.Select(a => new R7Evaluation.AccountInput(root.Key, a.AccountId,
            ObservationEncoding.Hash(counts[a.AccountId].SelectMany(BitConverter.GetBytes).ToArray()),
            episode.Rhythms?.GetValueOrDefault(a.AccountId), counts[a.AccountId],
            [.. episode.Events.Where(e => e.Subject == a.AccountId)])).ToArray();
        return new(episode, inputs, flows);
    }

    [Fact]
    [Trait("Category", "R7Diagnostics")]
    public void Census_generator_4_identifiability_on_development_plans()
    {
        var output = Environment.GetEnvironmentVariable("REVENANT_R7_DIAGNOSTICS_OUTPUT")
            ?? throw new InvalidOperationException("Set REVENANT_R7_DIAGNOSTICS_OUTPUT.");
        Directory.CreateDirectory(output);
        var roots = V4LongitudinalPlannerG4.Roots(LongitudinalSplit.Pilot).Select(Planned).ToArray();
        var warmup = R7QualificationEvaluationTests.AnomalyCandidates[0].Spec.Warmup;
        var spec = R7QualificationEvaluationTests.AnomalyCandidates[0].Spec;
        var profiles = roots.SelectMany(r => r.Accounts.Where(a => SsaAdvisory.EligibleAccount(spec, a.Counts)).SelectMany(a =>
            a.Labels.Where(e => e.Start >= warmup).Select(e => (Account: a, Event: e)))).ToArray();
        var scored = profiles.Select(x => (x.Account, x.Event, Profile: Profile(x.Account.Counts, x.Event))).ToArray();
        bool Positive((R7Evaluation.AccountInput Account, LongitudinalEvent Event, R7Diagnostics.EventProfile Profile) x) =>
            R7Evaluation.IsPositive(x.Event);
        bool Hard((R7Evaluation.AccountInput Account, LongitudinalEvent Event, R7Diagnostics.EventProfile Profile) x) =>
            x.Event.Kind == LongitudinalEventKind.LegitimateBurst;
        var features = new (string Name, Func<R7Diagnostics.EventProfile, double> Value)[]
        {
            ("EventMaxCount", e => e.Max), ("EventTotal", e => e.Total), ("SeasonalExcess", e => e.SeasonalExcess),
            ("NegativeTrailingRank", e => -e.TrailingRankOfMax), ("StartTimeOfDay", e => e.Start % 288),
        };
        R7RetainedPilotDiagnosticsTests.Write(output, "generator-4-census.json", new
        {
            Generator = V4LongitudinalPlannerG4.Version, Roots = roots.Select(r => new
            {
                r.Episode.Root, Payments = r.Episode.Financial.Payments.Length,
                MaxIntentsPerBucket = r.Episode.Financial.Payments.GroupBy(p => p.Intent.EventTimeUtcMs / Bucket).Max(g => g.Count()),
                Eligible = r.Accounts.Count(a => SsaAdvisory.EligibleAccount(spec, a.Counts)),
                ByRhythm = r.Accounts.GroupBy(a => a.Rhythm).Select(g => new
                {
                    Rhythm = g.Key, Accounts = g.Count(), MeanIntentsPerDay = g.Average(a => a.Counts.Sum() / 7d),
                    Eligible = g.Count(a => SsaAdvisory.EligibleAccount(spec, a.Counts)),
                    MedianDailyProfileCorrelation = Median(g.Select(a => R7Diagnostics.DailyProfilePersistence(a.Counts))),
                }),
                Events = r.Episode.Events.GroupBy(e => (e.Kind, e.Truth, e.Mechanism, e.Intent)).Select(g => new
                {
                    g.Key.Kind, g.Key.Truth, g.Key.Mechanism, g.Key.Intent, Count = g.Count(),
                }).OrderBy(x => x.Kind).ThenBy(x => x.Mechanism),
                BankChangeAutocorrelation = r.BankChanges.ToDictionary(b => b.Key, b => R7Diagnostics.Autocorrelation(
                    b.Value.Select(v => (double)v).ToArray(), [1, 2, 3, 4, 5, 6, 8, 10, 12, 24])),
            }),
            ScoredEvents = new
            {
                Positives = scored.Count(Positive), HardNegatives = scored.Count(Hard),
                Excluded = scored.Count(x => R7Evaluation.IsExcluded(x.Event)),
                PositiveVersusHardNegative = features.Select(f => new
                {
                    f.Name, Auc = R7Diagnostics.Auc(scored.Where(Positive).Select(x => f.Value(x.Profile)),
                        scored.Where(Hard).Select(x => f.Value(x.Profile))),
                }),
                PositiveMaxCount = Histogram(scored.Where(Positive).Select(x => x.Profile.Max)),
                HardNegativeMaxCount = Histogram(scored.Where(Hard).Select(x => x.Profile.Max)),
                PositiveTailProbability = scored.Where(Positive).Select(x => x.Event.TailProbability).Order().ToArray(),
                ExcludedTailProbability = scored.Where(x => R7Evaluation.IsExcluded(x.Event)).Select(x => x.Event.TailProbability).Order(),
            },
        });

        static R7Diagnostics.EventProfile Profile(long[] counts, LongitudinalEvent e)
        {
            var window = counts[e.Start..(e.End + 1)];
            var trailing = counts[Math.Max(0, e.Start - 288)..e.Start];
            var expected = Enumerable.Range(e.Start, e.End - e.Start + 1).Sum(b => R7Diagnostics.SeasonalMean(counts, b, e.Start));
            return new(e.Id, e.Subject, e.Kind, e.Truth?.ToString(), e.Start, window.Length, window, window.Sum(), window.Max(), 0, 0, 0,
                expected, window.Sum() - expected, (trailing.Count(c => c >= window.Max()) + 1d) / (trailing.Length + 1), true);
        }

        static Dictionary<long, int> Histogram(IEnumerable<long> values) => values.GroupBy(v => v).OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());

        static double? Median(IEnumerable<double?> values)
        {
            var v = values.Where(x => x.HasValue).Select(x => x!.Value).Order().ToArray();
            return v.Length == 0 ? null : v[v.Length / 2];
        }
    }

    [Fact]
    [Trait("Category", "R7DiagnosticsNative")]
    public void Develop_and_measure_anomaly_candidates_on_plan_derived_generator_4_counts()
    {
        Assert.True(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64);
        var output = Environment.GetEnvironmentVariable("REVENANT_R7_DIAGNOSTICS_OUTPUT")
            ?? throw new InvalidOperationException("Set REVENANT_R7_DIAGNOSTICS_OUTPUT.");
        Directory.CreateDirectory(output);
        var roots = V4LongitudinalPlannerG4.Roots(LongitudinalSplit.Pilot).Select(Planned).ToArray();
        var ml = new MLContext(seed: 7);
        // Root 0 trains, roots 1–2 select and calibrate, roots 3–5 measure.
        R7Evaluation.AccountInput[] calibrationInputs = [.. roots[1].Accounts, .. roots[2].Accounts];
        R7Evaluation.AccountInput[] measureInputs = [.. roots[3].Accounts, .. roots[4].Accounts, .. roots[5].Accounts];
        var development = R7QualificationEvaluationTests.DevelopAnomaly(ml, roots[0].Accounts, calibrationInputs, calibrationInputs,
            V4LongitudinalPlannerG4.Version);
        var budget = R7Evaluation.FalseAlertBudget;
        var trainingAccount = R7QualificationEvaluationTests.TrainingAccount(roots[0].Accounts,
            R7QualificationEvaluationTests.AnomalyCandidates[0].Spec);
        var candidates = R7QualificationEvaluationTests.AnomalyCandidates;
        var from = candidates.Max(c => c.Spec.Warmup);
        R7Evaluation.AccountCase[] Trim(R7Evaluation.AccountCase[] cases) =>
            [.. cases.Select(c => c with { Steps = [.. c.Steps.Where(s => s.Bucket >= from)] })];
        var fitted = new Dictionary<SsaAnomalySpec, byte[]>();
        var measured = candidates.Select(candidate =>
        {
            if (!fitted.TryGetValue(candidate.Spec, out var artifact))
                fitted[candidate.Spec] = artifact = RevenantSsaInference.FitAnomaly(ml, candidate.Spec, trainingAccount.Counts, 0);
            var policy = candidate.Policy();
            var calibration = Trim(R7QualificationEvaluationTests.Accounts(ml, calibrationInputs, artifact, candidate.Spec, policy));
            var evaluation = Trim(R7QualificationEvaluationTests.Accounts(ml, measureInputs, artifact, candidate.Spec, policy));
            return new
            {
                Candidate = candidate.Name, ArtifactSha256 = ObservationEncoding.Hash(artifact),
                Budgets = R7QualificationEvaluationTests.SensitivityBudgets.Select(b =>
                {
                    var target = b * R7QualificationEvaluationTests.CalibrationMargin;
                    var ssa = R7Evaluation.Calibrate(calibration, R7Evaluation.Scorer.Ssa, policy, target);
                    var rank = R7Evaluation.Calibrate(calibration, R7Evaluation.Scorer.TrailingRank, null, target);
                    var seasonal = R7Evaluation.Calibrate(calibration, R7Evaluation.Scorer.SeasonalExcess, null, target);
                    return new
                    {
                        Budget = b, SsaThreshold = ssa.Threshold, RankThreshold = rank.Threshold, SeasonalThreshold = seasonal.Threshold,
                        Ssa = R7Evaluation.Evaluate(evaluation, R7Evaluation.Scorer.Ssa, policy, ssa.Threshold),
                        Rank = R7Evaluation.Evaluate(evaluation, R7Evaluation.Scorer.TrailingRank, null, rank.Threshold),
                        Seasonal = R7Evaluation.Evaluate(evaluation, R7Evaluation.Scorer.SeasonalExcess, null, seasonal.Threshold),
                    };
                }).ToArray(),
            };
        }).ToArray();
        R7RetainedPilotDiagnosticsTests.Write(output, "generator-4-anomaly-development.json", new
        {
            Evidence = "plan-derived counts, generator 4 Pilot roots; root 0 trains, roots 1-2 select and calibrate, roots 3-5 measure",
            Roots = roots.Select(r => r.Episode.Root.Key), Budget = budget, Selection = development.Report, Measured = measured,
            Response = candidates.Where(c => c.Statistic == SsaAlertStatistic.Residual).DistinctBy(c => c.Spec).Select(c =>
                Response(ml, c, trainingAccount.Counts, roots[3].Accounts)).ToArray(),
        });
    }

    /// <summary>
    /// Forecast development on the PostgreSQL-executed generator-4 pilot (roots 0–2): root 0 trains, root 1 selects and calibrates
    /// the conformal band, root 2 measures. One-factor variants isolate representation, window, rank, training length and the
    /// training-bank rule. A least-squares AR on changes gives the linear predictability reference for the same origins.
    /// </summary>
    [Fact]
    [Trait("Category", "R7DiagnosticsNative")]
    public void Develop_and_measure_forecast_candidates_on_generator_4_PostgreSQL_streams()
    {
        Assert.True(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64);
        var input = Environment.GetEnvironmentVariable("REVENANT_R7_PILOT_INPUT")
            ?? throw new InvalidOperationException("Set REVENANT_R7_PILOT_INPUT to the generator-4 PostgreSQL pilot.");
        var output = Environment.GetEnvironmentVariable("REVENANT_R7_DIAGNOSTICS_OUTPUT")
            ?? throw new InvalidOperationException("Set REVENANT_R7_DIAGNOSTICS_OUTPUT.");
        Directory.CreateDirectory(output);
        var roots = ReadExecutedPilot(input);
        var ml = new MLContext(seed: 7);
        var development = R7QualificationEvaluationTests.DevelopForecast(ml, [roots[0]], [roots[1]], [roots[1]],
            V4LongitudinalPlannerG4.Version);

        var change = SsaForecastRepresentation.CompletedIntervalChange;
        var level = SsaForecastRepresentation.OpeningCenteredLevel;
        var maxVariance = R7QualificationEvaluationTests.TrainingBank([roots[0]]);
        var calm = roots[0].Banks.Where(b => !roots[0].Events.Any(e => e.Subject == b.SubjectId))
            .OrderBy(b => b.SubjectId, StringComparer.Ordinal).First();
        var variants = new (string Name, string Factor, SsaForecastSpec Spec, V4AdvisorySeries Bank)[]
        {
            ("change-w24", "selected design", new(change, 24, 96, 2016, null, 288, 1_000), maxVariance),
            ("level-w24", "representation", new(level, 24, 96, 2016, null, 288, 1_000), maxVariance),
            ("change-w12", "window", new(change, 12, 48, 2016, null, 288, 1_000), maxVariance),
            ("change-w48", "window", new(change, 48, 192, 2016, null, 288, 1_000), maxVariance),
            ("change-w24-rank6", "fixed rank", new(change, 24, 96, 2016, 6, 288, 1_000), maxVariance),
            ("change-w24-train288", "training length", new(change, 24, 96, 288, null, 288, 1_000), maxVariance),
            ("change-w24-calm-bank", "training bank rule", new(change, 24, 96, 2016, null, 288, 1_000), calm),
        };
        var placeholder = new SsaIntervalPolicy(R7QualificationEvaluationTests.SigmaWindow, .95, [.. new double[8]]);
        var measured = variants.Select(v =>
        {
            var artifact = RevenantSsaInference.FitForecast(ml, v.Spec, v.Bank.ReferenceMinor!.Value,
                v.Bank.Points.Select(p => p.Value).ToArray(), 0);
            var calibration = R7QualificationEvaluationTests.Banks(ml, [roots[1]], artifact, v.Spec, placeholder);
            var multipliers = R7Evaluation.ConformalMultipliers(calibration.Select(b => (b.Steps, (Func<int, int, long?>)b.Case.Target)), .95);
            var interval = placeholder with { Multipliers = [.. multipliers] };
            var cases = R7QualificationEvaluationTests.Banks(ml, [roots[2]], artifact, v.Spec, interval).Select(b => b.Case).ToArray();
            var horizons = R7ForecastMetrics.Evaluate(cases);
            return new
            {
                v.Name, v.Factor, v.Spec, TrainingBank = v.Bank.SubjectId, ArtifactSha256 = ObservationEncoding.Hash(artifact),
                Multipliers = multipliers, Skill = R7QualificationEvaluationTests.Skill(horizons),
                Ratio = R7QualificationEvaluationTests.OverallRatio(horizons), Warnings = R7ForecastMetrics.Warnings(cases),
                Horizons = R7RetainedPilotDiagnosticsTests.Summaries(horizons),
                Coverage = horizons.Select(h => new { h.Horizon, h.CalibratedCoverage, h.CalibratedWidth, h.StressCalibratedCoverage,
                    h.ActiveCalibratedCoverage, h.OrdinaryCalibratedCoverage }),
                ByBank = cases.Select(c => new { c.Bank, Horizons = R7ForecastMetrics.Evaluate([c]) }),
                ByStressType = new[] { LongitudinalEventKind.DevelopingPressure, LongitudinalEventKind.SuddenShock }.Select(kind => new
                {
                    Kind = kind.ToString(),
                    Horizons = R7ForecastMetrics.Evaluate(cases.Select(c => c with
                    {
                        Events = [.. c.Events.Where(e => e.Kind == kind)],
                    }).ToArray()).Select(h => new { h.Horizon, h.Stress, h.StressMae, h.StressBias, h.StressPersistenceMae, h.StressRobustMae }),
                }),
            };
        }).ToArray();

        var train = roots[0].Banks.Select(b => R7Diagnostics.Changes(b.ReferenceMinor!.Value, b.Points.Select(p => p.Value).ToArray(),
            SsaAdvisory.Scale(b.ReferenceMinor!.Value, 1_000)));
        var linear = new[] { 12, 24 }.Select(order => new
        {
            Order = order,
            Summary = R7RetainedPilotDiagnosticsTests.Summaries(R7ForecastMetrics.Evaluate(
                R7RetainedPilotDiagnosticsTests.ArCases(roots[2], R7Diagnostics.FitAutoregression(train, order, 1)))),
        }).ToArray();
        R7RetainedPilotDiagnosticsTests.Write(output, "generator-4-forecast-development.json", new
        {
            Evidence = "generator 4 PostgreSQL pilot; root 0 trains, root 1 selects and calibrates, root 2 measures",
            Manifest = ObservationEncoding.Hash(File.ReadAllBytes(Path.Combine(input, "pilot.json"))),
            Selection = development.Report, Variants = measured, LinearReference = linear,
            Banks = roots.SelectMany((r, i) => r.Banks.Select(b => new
            {
                Root = i, b.SubjectId, Events = r.Events.Where(e => e.Subject == b.SubjectId).Select(e => (e.Kind, e.Start, e.End)),
                ChangeAutocorrelation = R7Diagnostics.Autocorrelation(R7Diagnostics.Changes(b.ReferenceMinor!.Value,
                    b.Points.Select(p => p.Value).ToArray(), SsaAdvisory.Scale(b.ReferenceMinor!.Value, 1_000)), [1, 2, 3, 4, 5, 6, 8, 10, 12, 24]),
            })),
        });
    }

    /// <summary>What the fitted recurrence expects: impulse and periodic responses, and residuals by event class.</summary>
    private static object Response(MLContext ml, R7QualificationEvaluationTests.AnomalyCandidate candidate, long[] training,
        R7Evaluation.AccountInput[] accounts)
    {
        var spec = candidate.Spec with { MinimumNonzero = 0 };
        var artifact = RevenantSsaInference.FitAnomaly(ml, spec, training, 0);
        var policy = new SsaAlertPolicy(SsaAlertStatistic.Residual, 1e9, 0);
        long[] impulse = [.. new long[spec.Warmup + 24], 3, .. new long[320]];
        var impulseSteps = RevenantSsaInference.ScoreAccount(ml, artifact, spec, policy, impulse);
        var at = spec.Warmup + 24;
        var periodic = Enumerable.Range(0, 5 * 288).Select(i => i % 288 is >= 100 and < 110 ? 3L : 0L).ToArray();
        var periodicSteps = RevenantSsaInference.ScoreAccount(ml, artifact, spec, policy, periodic);
        var classes = accounts.Select(a => (a, Steps: RevenantSsaInference.ScoreAccount(ml, artifact, spec, policy, a.Counts)))
            .Where(x => x.Steps.Length > 0).SelectMany(x => x.a.Labels.Where(e => e.Start >= x.Steps[0].Bucket && e.Truth is not null)
                .Select(e => (Class: e.Kind == LongitudinalEventKind.LegitimateBurst ? "HardNegative" : $"{e.Truth}/{e.Mechanism}",
                    Steps: x.Steps.Where(s => s.Bucket >= e.Start && s.Bucket <= e.End).ToArray())))
            .GroupBy(x => x.Class).Select(g => new
            {
                Class = g.Key, Events = g.Count(),
                MeanMaxResidual = g.Average(x => x.Steps.Max(s => s.Residual)),
                MeanExpectedAtStart = g.Average(x => x.Steps[0].ExpectedCount), MeanCountAtStart = g.Average(x => (double)x.Steps[0].Count),
            }).ToArray();
        return new
        {
            candidate.Name, Spec = spec,
            ImpulseExpected = impulseSteps.Where(s => s.Bucket > at && s.Bucket <= at + 300)
                .Select(s => (Lag: s.Bucket - at, s.ExpectedCount)).Where(x => x.ExpectedCount > .05).ToArray(),
            PeriodicExpectedDay5 = periodicSteps.Where(s => s.Bucket >= 4 * 288 + 95 && s.Bucket < 4 * 288 + 115)
                .Select(s => Math.Round(s.ExpectedCount, 2)).ToArray(),
            Classes = classes,
        };
    }
}
