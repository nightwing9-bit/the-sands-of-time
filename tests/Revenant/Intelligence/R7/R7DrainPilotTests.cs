using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Hosts.Worker;
using Microsoft.ML;
using Revenant.Contracts;
using Revenant.Observations;
using Revenant.Synthetic;
using Xunit;

namespace Revenant.Tests.Intelligence.R7;

public sealed partial class R7Generator4DevelopmentTests
{
    [Fact]
    [Trait("Category", "R7Diagnostics")]
    public void Check_G5_planned_operational_flow_feasibility()
    {
        var (oldRoots, output, _) = DiagnosticInput();
        foreach (var old in oldRoots)
        {
            var plan = V4LongitudinalPlannerG4.Plan(old.Manifest.Root, new(old.Manifest.Execution));
            Assert.Equal(old.Manifest.BehaviorSha256, plan.BehaviorSha256);
            Assert.Equal(old.Manifest.ContentHash, plan.Financial.Opening.Manifest.ContentHash);
            Assert.Equal(old.Events, plan.Events.ToArray());
        }
        var roots = V4LongitudinalPlannerG5.Roots().Select(PlanSample).ToArray();
        var feasibility = Enumerable.Range(0, 3).Select(i =>
        {
            var train = roots[i];
            var measure = roots[(i + 2) % 3];
            var threshold = Materiality(train);
            var rows = DrainRows(measure, threshold);
            var median = FitMedian(DrainRows(train, threshold));
            var levels = measure.Banks.ToDictionary(b => b.SubjectId, Values);
            long Predict(DrainRow row, int m, int h) => m switch
            {
                0 => row.Current,
                1 => row.State.Active ? row.Drift[h] : row.Current,
                2 => MedianPrediction(row, h, median),
                3 => R7Evaluation.TrailingMedian(levels[row.Bank], row.Bucket),
                4 => checked(row.Current + checked(levels[row.Bank][row.Bucket + h + 1 - DailyBuckets]
                    - levels[row.Bank][row.Bucket - DailyBuckets])),
                _ => throw new ArgumentOutOfRangeException(nameof(m)),
            };
            return new
            {
                Train = train.Manifest.Root.Key, Measurement = measure.Manifest.Root.Key, Threshold = threshold,
                States = CountStates(rows), Episodes = AuditEpisodes(measure, threshold, rows),
                Scores = ReportScores(rows, ["Persistence", "GatedDrift", "ConditionalMedian", "TrailingMedian", "SeasonalNaive"], Predict),
            };
        }).ToArray();
        R7RetainedPilotDiagnosticsTests.Write(output, "d3-planned-feasibility.json", new
        {
            Generator = V4LongitudinalPlannerG5.Version,
            Scope = "Nominal planned settlement walk. Real review, reservation, settlement and return effects require PostgreSQL.",
            Roots = roots.Select(r => r.Manifest), Feasibility = feasibility,
        });
    }

    [Fact]
    [Trait("Category", "R7DiagnosticsNative")]
    public void Measure_frozen_drain_comparison_on_executed_G5()
    {
        Assert.True(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64);
        var input = Environment.GetEnvironmentVariable("REVENANT_R7_G5_INPUT")
            ?? throw new InvalidOperationException("Set REVENANT_R7_G5_INPUT to the isolated PostgreSQL export.");
        var output = Environment.GetEnvironmentVariable("REVENANT_R7_DIAGNOSTICS_OUTPUT")
            ?? throw new InvalidOperationException("Set REVENANT_R7_DIAGNOSTICS_OUTPUT.");
        Directory.CreateDirectory(output);
        var manifest = LongitudinalSample.ReadManifest(input, "pilot.json");
        Assert.Equal(V4LongitudinalPlannerG5.Version, manifest.Generator);
        Assert.Equal(V4LongitudinalPlannerG5.Roots(), manifest.Roots.Select(r => r.Root));
        var roots = manifest.Roots.Select(r => LongitudinalSample.Read(input, r, requirePilotTargets: true, readFlows: true)).ToArray();
        foreach (var root in roots)
        {
            var total = root.Banks.Sum(b => b.ReferenceMinor!.Value);
            Assert.Equal(root.Manifest.Accepted, root.Accounts.Sum(a => a.Points.Sum(p => p.Value)));
            for (var bucket = 1; bucket < root.Manifest.Seals; bucket++)
                Assert.Equal(total, root.Flows!.Values.Sum(flows => flows[bucket]!.Balance.Posted));
        }
        var ml = new MLContext(seed: 7);
        const int window = 3;
        var rotations = Enumerable.Range(0, roots.Length).Select(i =>
        {
            var train = roots[i];
            var measure = roots[(i + 2) % 3];
            var training = R7QualificationEvaluationTests.TrainingBank([train with
            {
                Banks = [.. train.Banks.Where(b => train.Events.Any(e => e.Subject == b.SubjectId && e.Kind == LongitudinalEventKind.DevelopingPressure))],
            }]);
            var spec = R7QualificationEvaluationTests.ForecastCandidates[0].Spec with { TrainSize = training.Points.Length - window + 1 };
            var zip = RevenantSsaInference.FitTrailingRate(ml, spec, training.ReferenceMinor!.Value, Values(training), window);
            File.WriteAllBytes(Path.Combine(output, $"g5-root-{i}-three-bucket.zip"), zip);
            var comparison = GatedRotation(train, roots[(i + 1) % 3], measure, (_, bank) =>
                {
                    var levels = Values(bank);
                    var forecasts = RevenantSsaInference.ForecastTrailingRate(ml, zip, spec, bank.ReferenceMinor!.Value, levels, window);
                    var prefix = RevenantSsaInference.ForecastTrailingRate(ml, zip, spec, bank.ReferenceMinor.Value, levels[..(CoreOrigin + 1)], window)[^1];
                    Assert.Equal(forecasts.Single(s => s.Bucket == CoreOrigin).ExactInverse, prefix.ExactInverse);
                    return forecasts.Where(s => s.Bucket >= CoreOrigin)
                        .ToDictionary(s => s.Bucket, s => new ForecastPaths(s.ExactInverse, s.ExactInverse));
                }, ObservationEncoding.Hash(zip), training.SubjectId);
            return new
            {
                TrainingSeriesSha256 = training.Digest, Spec = spec,
                Episodes = AuditEpisodes(measure, Materiality(train), DrainRows(measure, Materiality(train))),
                Results = comparison.Report, comparison.Pass,
            };
        }).ToArray();
        R7RetainedPilotDiagnosticsTests.Write(output, "d3-executed-ssa.json", new
        {
            ManifestSha256 = ObservationEncoding.Hash(File.ReadAllBytes(Path.Combine(input, "pilot.json"))), manifest.Revision,
            Platform = RuntimeInformation.RuntimeIdentifier, Runtime = RuntimeInformation.FrameworkDescription, Rotations = rotations,
            PassingRotations = rotations.Count(r => r.Pass), Pass = rotations.Count(r => r.Pass) >= 2,
        });
    }

    private static LongitudinalSample PlanSample(LongitudinalRoot root)
    {
        var id = new ScenarioRunId(new Guid(Convert.FromHexString(root.Key[..32]), bigEndian: true));
        var episode = V4LongitudinalPlannerG5.Plan(root, id);
        var plan = episode.Financial;
        var manifest = plan.Opening.Manifest;
        var seals = root.Days * DailyBuckets;
        var accounts = manifest.Accounts.ToDictionary(a => a.AccountId, a => a.OpeningBalance.MinorUnits);
        var flows = manifest.Banks.ToDictionary(b => b.BankId, _ => new long[seals]);
        foreach (var p in plan.Payments)
        {
            var intent = p.Intent;
            var amount = intent.Amount.MinorUnits;
            accounts[intent.SourceAccountId] -= amount;
            accounts[intent.DestAccountId] += amount;
            Assert.True(accounts[intent.SourceAccountId] >= 0);
            var bucket = (int)((intent.EventTimeUtcMs - manifest.StartUtcMs) / Bucket);
            flows[intent.SourceBankId][bucket] -= amount;
            flows[intent.DestBankId][bucket] += amount;
        }
        Assert.Equal(manifest.Accounts.Sum(a => a.OpeningBalance.MinorUnits), accounts.Values.Sum());
        Assert.Single(episode.Events.Where(e => e.Kind == LongitudinalEventKind.SuddenShock));
        Assert.Equal(6, episode.Events.Count(e => e.Kind == LongitudinalEventKind.DevelopingPressure));
        var banks = manifest.Banks.Select(bank =>
        {
            var opening = bank.OpeningBalance.MinorUnits;
            var level = opening;
            var points = flows[bank.BankId].Select(change => new AdvisorySeriesPoint(null!, level += change)).ToImmutableArray();
            return new V4AdvisorySeries(V4AdvisorySeries.BankSchema, id, bank.BankId, "USD",
                manifest.StartUtcMs, opening, plan.EndMs, AdvisorySeriesCoverage.Complete, points);
        }).ToArray();
        var targets = banks.ToDictionary(b => b.SubjectId, b => b.Points.Select((_, t) => Enumerable.Range(1, 8)
            .Select(h => t + h < b.Points.Length ? b.Points[t + h].Value : (long?)null).ToArray()).ToArray());
        return new(new(root, episode.BehaviorSha256, manifest.ContentHash, id.Value, seals, [], plan.Payments.Length, 0, 0, 0),
            [], banks, episode.Events.ToArray(), targets);
    }
}
