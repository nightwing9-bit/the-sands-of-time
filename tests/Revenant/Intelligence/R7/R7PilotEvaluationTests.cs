using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.ML;
using Revenant.Observations;
using Revenant.Synthetic;
using Xunit;

namespace Revenant.Tests.Intelligence.R7;

public sealed class R7PilotEvaluationTests
{
    [Fact]
    [Trait("Category", "R7Longitudinal")]
    public void Measure_SSA_transfer_on_the_frozen_seven_day_pilot()
    {
        Assert.True(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64);
        var input = Environment.GetEnvironmentVariable("REVENANT_R7_PILOT_INPUT")
            ?? throw new InvalidOperationException("Set the frozen pilot directory.");
        var output = Environment.GetEnvironmentVariable("REVENANT_R7_PILOT_EVAL")
            ?? throw new InvalidOperationException("Set the pilot evaluation directory.");
        Directory.CreateDirectory(output);
        var manifest = LongitudinalSample.ReadManifest(input, "pilot.json");
        Assert.Equal(V4LongitudinalPlanner.Roots(LongitudinalSplit.Pilot), manifest.Roots.Select(r => r.Root));
        var train = LongitudinalSample.Read(input, manifest.Roots[0]);
        var unseen = LongitudinalSample.Read(input, manifest.Roots[1]);
        var trainingAccount = train.Accounts.Where(R7RollingEvaluation.Eligible)
            .OrderByDescending(s => s.Points.Take(288).Count(p => p.Value > 0)).ThenBy(s => s.SubjectId, StringComparer.Ordinal).First();
        var trainingBank = train.Banks.OrderByDescending(s => s.Points.Take(288).Select(p => p.Value).Distinct().Count())
            .ThenBy(s => s.SubjectId, StringComparer.Ordinal).First();
        var ml = new MLContext(seed: 7);
        var report = new List<object>();
        var correction = Environment.GetEnvironmentVariable("REVENANT_R7_PILOT_CORRECTION") == "1";
        foreach (var window in correction ? new[] { 8 } : new[] { 8, 12 })
        {
            var anomalyConfig = new R7RollingEvaluation.AnomalyConfig(window, correction ? 1 : window * 2, 288);
            var forecastConfig = new R7RollingEvaluation.ForecastConfig(window, window * 4, 288,
                Rank: correction ? 2 : null, OriginCentered: correction);
            var anomalyPath = Path.Combine(output, $"pilot-anomaly-{window}.zip");
            var forecastPath = Path.Combine(output, $"pilot-forecast-{window}.zip");
            R7RollingEvaluation.FitAnomaly(ml, trainingAccount, anomalyConfig, anomalyPath);
            R7RollingEvaluation.FitForecast(ml, trainingBank, forecastConfig, forecastPath);
            var account = unseen.Accounts.Select(s => (s, R7RollingEvaluation.ScoreAccount(ml, s, anomalyPath),
                unseen.Events.Where(e => e.Subject == s.SubjectId).ToArray())).ToArray();
            var banks = unseen.Banks.Select(s => new R7ForecastMetrics.BankCase(unseen.Manifest.Root.Key, s.SubjectId,
                s.ReferenceMinor!.Value, s.Points.Select(p => p.Value).ToArray(),
                R7RollingEvaluation.ForecastBank(ml, s, forecastPath, forecastConfig.OriginCentered),
                unseen.Events.Where(e => e.Subject == s.SubjectId).ToArray())).ToArray();
            report.Add(new
            {
                Anomaly = anomalyConfig, Forecast = forecastConfig,
                AnomalySha256 = ObservationEncoding.Hash(File.ReadAllBytes(anomalyPath)),
                ForecastSha256 = ObservationEncoding.Hash(File.ReadAllBytes(forecastPath)),
                InvalidNativeScoreRuns = account.Select(a => new
                {
                    Subject = a.s.SubjectId,
                    Count = a.Item2.Count(s => s.Raw is null || s.PValue is null),
                    Examples = a.Item2.Where(s => s.Raw is null || s.PValue is null).Take(4),
                }).ToArray(),
                ThresholdDiagnostics = new[] { 1e-7, 1e-6, .0001, .001, .0025, .005, .01, .025, .05 }.Select(threshold => new
                {
                    Threshold = threshold,
                    Ssa = R7RollingEvaluation.EvaluateAnomalies(account, threshold, baseline: false),
                    Rank = R7RollingEvaluation.EvaluateAnomalies(account, threshold, baseline: true),
                }).ToArray(),
                Horizons = R7ForecastMetrics.Evaluate(banks, new long[R7RollingEvaluation.Horizon]),
                Warnings = R7ForecastMetrics.Warnings(banks),
            });
        }
        File.WriteAllBytes(Path.Combine(output, "pilot-evaluation.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            ManifestSha256 = ObservationEncoding.Hash(File.ReadAllBytes(Path.Combine(input, "pilot.json"))),
            Train = train.Manifest.Root, Unseen = unseen.Manifest.Root,
            TrainingAccount = trainingAccount.SubjectId, TrainingBank = trainingBank.SubjectId,
            TrainingAccountSha256 = trainingAccount.Digest, TrainingBankSha256 = trainingBank.Digest,
            Candidates = report,
        }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
    }
}
