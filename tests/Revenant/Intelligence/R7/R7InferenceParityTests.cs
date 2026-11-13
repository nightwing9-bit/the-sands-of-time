using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using Hosts.Worker;
using Microsoft.ML;
using Revenant.Contracts;
using Revenant.Intelligence;
using Revenant.Observations;
using Revenant.Timeline;
using Xunit;

namespace Revenant.Tests.Intelligence.R7;

/// <summary>
/// Train/serve equality for R7/2 on committed series (the retained pilot streams). The published Worker's scoring function, called
/// on a causal prefix, must equal the research/qualification output at that bucket bit for bit; a cold restart must reproduce the
/// same evidence bytes; and a child whose history diverges after a fork must agree with its source on every inherited origin.
/// </summary>
public sealed class R7InferenceParityTests
{
    [Fact]
    [Trait("Category", "R7Parity")]
    public void Worker_prefix_scoring_equals_research_scoring_and_is_causal_and_restart_stable()
    {
        Assert.True(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64);
        var input = Environment.GetEnvironmentVariable("REVENANT_R7_PILOT_INPUT")
            ?? throw new InvalidOperationException("Set REVENANT_R7_PILOT_INPUT to committed longitudinal streams.");
        var manifest = LongitudinalSample.ReadManifest(input, "pilot.json");
        var root = LongitudinalSample.Read(input, manifest.Roots[0]);
        var other = LongitudinalSample.Read(input, manifest.Roots[^1]);
        var ml = new MLContext(seed: 7);
        var directory = Directory.CreateTempSubdirectory("r7-parity-").FullName;
        try
        {
            var anomalyCandidate = R7QualificationEvaluationTests.AnomalyCandidates[0];
            var account = root.Accounts.OrderByDescending(a => a.Points.Count(p => p.Value > 0)).First();
            var counts = account.Points.Select(p => p.Value).ToArray();
            var anomalyArtifact = RevenantSsaInference.FitAnomaly(ml, anomalyCandidate.Spec, counts, 0);
            var anomalyPin = Pin(directory, "anomaly", anomalyArtifact, new(SsaAdvisory.Policy, "anomaly",
                ObservationEncoding.Hash(anomalyArtifact), "parity-fixture", account.Digest, anomalyCandidate.Spec,
                anomalyCandidate.Policy(4), null, null));
            var forecastSpec = R7QualificationEvaluationTests.ForecastCandidates[0].Spec;
            var bank = root.Banks[0];
            var levels = bank.Points.Select(p => p.Value).ToArray();
            var forecastArtifact = RevenantSsaInference.FitForecast(ml, forecastSpec, bank.ReferenceMinor!.Value, levels, 0);
            var interval = new SsaIntervalPolicy(12, .95, [1, 1.5, 2, 2.5, 3, 3.5, 4, 4.5]);
            var forecastPin = Pin(directory, "forecast", forecastArtifact, new(SsaAdvisory.Policy, "forecast",
                ObservationEncoding.Hash(forecastArtifact), "parity-fixture", bank.Digest, null, null, forecastSpec, interval));

            foreach (var subject in other.Accounts.Where(a => SsaAdvisory.EligibleAccount(anomalyCandidate.Spec,
                         a.Points.Select(p => p.Value).ToArray())).Take(3))
            {
                var values = subject.Points.Select(p => p.Value).ToArray();
                var research = RevenantSsaInference.ScoreAccount(ml, anomalyArtifact, anomalyCandidate.Spec, anomalyCandidate.Policy(4), values);
                foreach (var k in new[] { anomalyCandidate.Spec.Warmup, anomalyCandidate.Spec.Warmup + 137, values.Length - 1 })
                {
                    var evidence = RevenantAdvisoryWorker.Score(Prefix(subject, k), subject.ExecutionRunId, anomalyPin, true, directory);
                    Assert.Equal(AdvisoryStatus.Available, evidence.Status);
                    var expected = research[k - anomalyCandidate.Spec.Warmup];
                    var actual = evidence.AnomalyV2!;
                    Assert.Equal((expected.Count, expected.ExpectedCount, expected.Residual, expected.Statistic, expected.Alert),
                        (actual.Count, actual.ExpectedCount, actual.Residual, actual.StatisticValue, actual.Alert));
                    var cold = RevenantAdvisoryWorker.Score(Prefix(subject, k), subject.ExecutionRunId, anomalyPin, true, directory);
                    Assert.Equal(evidence.Encode(), cold.Encode());
                }
                Assert.Equal(AdvisoryStatus.InsufficientHistory, RevenantAdvisoryWorker.Score(Prefix(subject,
                    anomalyCandidate.Spec.Warmup - 1), subject.ExecutionRunId, anomalyPin, true, directory).Status);
            }

            foreach (var subject in other.Banks.Take(3))
            {
                var values = subject.Points.Select(p => p.Value).ToArray();
                var research = RevenantSsaInference.ForecastBank(ml, forecastArtifact, forecastSpec, interval, subject.ReferenceMinor!.Value, values);
                foreach (var k in new[] { forecastSpec.Warmup, 1_000, values.Length - 1 })
                {
                    var evidence = RevenantAdvisoryWorker.Score(Prefix(subject, k), subject.ExecutionRunId, forecastPin, false, directory);
                    Assert.Equal(AdvisoryStatus.Available, evidence.Status);
                    var expected = research[k - forecastSpec.Warmup];
                    var actual = evidence.ForecastV2!;
                    Assert.Equal(expected.Central, actual.Horizons.Select(h => h.CentralMinor));
                    Assert.Equal(expected.CalibratedLower, actual.Horizons.Select(h => h.CalibratedLowerMinor));
                    Assert.Equal(expected.CalibratedUpper, actual.Horizons.Select(h => h.CalibratedUpperMinor));
                    Assert.Equal(expected.Sigma, actual.SigmaMinor);
                    Assert.Equal(evidence.Encode(),
                        RevenantAdvisoryWorker.Score(Prefix(subject, k), subject.ExecutionRunId, forecastPin, false, directory).Encode());
                }

                // A child that holds 175,000 minor units from bucket 1,200 inherits every earlier origin unchanged.
                const int fork = 1_200;
                var child = values.Select((v, i) => i >= fork ? Math.Max(0, v - 175_000) : v).ToArray();
                var childSteps = RevenantSsaInference.ForecastBank(ml, forecastArtifact, forecastSpec, interval, subject.ReferenceMinor!.Value, child);
                for (var i = 0; i < fork - forecastSpec.Warmup; i++)
                    Assert.Equal(research[i].Central, childSteps[i].Central);
                Assert.NotEqual(research[fork - forecastSpec.Warmup].Central, childSteps[fork - forecastSpec.Warmup].Central);
            }

            var tampered = Path.Combine(directory, "forecast", forecastPin.ModelId.Split('/')[1], "forecast.zip");
            var corrupt = (byte[])forecastArtifact.Clone();
            corrupt[^1] ^= 0xFF;
            File.WriteAllBytes(tampered, corrupt);
            Assert.Equal(AdvisoryStatus.ArtifactInvalid, RevenantAdvisoryWorker.Score(Prefix(other.Banks[0], 1_000),
                other.Banks[0].ExecutionRunId, forecastPin, false, directory).Status);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static V4AdvisorySeries Prefix(V4AdvisorySeries series, int index) => series with
    {
        Points = ImmutableArray.Create(series.Points.AsSpan(0, index + 1)),
        RequestedBoundaryMs = series.Points[index].Seal.BoundaryMs,
    };

    private static ObservedModelPin Pin(string root, string kind, byte[] artifact, SsaAdvisoryManifest manifest)
    {
        Assert.True(manifest.IsValidFor(kind));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, RevenantAdvisoryWorker.ManifestJson);
        var hash = ObservationEncoding.Hash(bytes);
        var directory = Path.Combine(root, kind, hash);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "manifest.json"), bytes);
        File.WriteAllBytes(Path.Combine(directory, kind + ".zip"), artifact);
        return new($"revenant-ssa-{kind}/{hash}", ObservationEncoding.Hash(artifact));
    }
}
