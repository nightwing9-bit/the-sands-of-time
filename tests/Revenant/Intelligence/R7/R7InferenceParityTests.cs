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
                anomalyCandidate.Policy(4), null, null, SsaAdvisoryManifest.CausalAnomalyExpectation));
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
                const int extra = 32;
                long[] differentHistory = [.. Enumerable.Repeat(3L, extra), .. values];
                var transferred = RevenantSsaInference.ScoreAccount(ml, anomalyArtifact, anomalyCandidate.Spec,
                    anomalyCandidate.Policy(4), differentHistory);
                Assert.InRange(Math.Abs(research[0].ExpectedCount - transferred[extra].ExpectedCount), 0, 1e-6);
                var origin = anomalyCandidate.Spec.Warmup;
                var perturbed = values[..(origin + 1)];
                perturbed[^1] += 9;
                var currentSpike = RevenantSsaInference.ScoreAccount(ml, anomalyArtifact, anomalyCandidate.Spec,
                    anomalyCandidate.Policy(4), perturbed).Single();
                Assert.InRange(Math.Abs(research[0].ExpectedCount - currentSpike.ExpectedCount), 0, 1e-6);
                foreach (var k in new[] { anomalyCandidate.Spec.Warmup, anomalyCandidate.Spec.Warmup + 137, values.Length - 1 })
                {
                    var evidence = ScoreHistorical(Prefix(subject, k), subject.ExecutionRunId, anomalyPin, true, directory);
                    Assert.Equal(AdvisoryStatus.Available, evidence.Status);
                    var expected = research[k - anomalyCandidate.Spec.Warmup];
                    var actual = evidence.AnomalyV2!;
                    Assert.Equal((expected.Count, expected.ExpectedCount, expected.Residual, expected.Statistic, expected.Alert),
                        (actual.Count, actual.ExpectedCount, actual.Residual, actual.StatisticValue, actual.Alert));
                    var cold = ScoreHistorical(Prefix(subject, k), subject.ExecutionRunId, anomalyPin, true, directory);
                    Assert.Equal(evidence.Encode(), cold.Encode());
                }
                Assert.Equal(AdvisoryStatus.InsufficientHistory, ScoreHistorical(Prefix(subject,
                    anomalyCandidate.Spec.Warmup - 1), subject.ExecutionRunId, anomalyPin, true, directory).Status);
            }

            foreach (var subject in other.Banks.Take(3))
            {
                var values = subject.Points.Select(p => p.Value).ToArray();
                var research = RevenantSsaInference.ForecastBank(ml, forecastArtifact, forecastSpec, interval, subject.ReferenceMinor!.Value, values);
                const int extra = 32;
                long[] differentHistory = [.. Enumerable.Repeat(subject.ReferenceMinor.Value + 175_000, extra), .. values];
                var transferred = RevenantSsaInference.ForecastBank(ml, forecastArtifact, forecastSpec, interval,
                    subject.ReferenceMinor.Value, differentHistory);
                Assert.Equal(research[0].Central, transferred[extra].Central);
                foreach (var k in new[] { forecastSpec.Warmup, 1_000, values.Length - 1 })
                {
                    var evidence = ScoreHistorical(Prefix(subject, k), subject.ExecutionRunId, forecastPin, false, directory);
                    Assert.Equal(AdvisoryStatus.Available, evidence.Status);
                    var expected = research[k - forecastSpec.Warmup];
                    var actual = evidence.ForecastV2!;
                    Assert.Equal(expected.Central, actual.Horizons.Select(h => h.CentralMinor));
                    Assert.Equal(expected.CalibratedLower, actual.Horizons.Select(h => h.CalibratedLowerMinor));
                    Assert.Equal(expected.CalibratedUpper, actual.Horizons.Select(h => h.CalibratedUpperMinor));
                    Assert.Equal(expected.Sigma, actual.SigmaMinor);
                    Assert.Equal(evidence.Encode(),
                        ScoreHistorical(Prefix(subject, k), subject.ExecutionRunId, forecastPin, false, directory).Encode());
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
            Assert.Equal(AdvisoryStatus.ArtifactInvalid, ScoreHistorical(Prefix(other.Banks[0], 1_000),
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

    // Archived /2 forecasting parity adapter; outside the published Worker.
    private static AdvisoryEvidence ScoreHistorical(V4AdvisorySeries series, ScenarioRunId lineage, ObservedModelPin pin, bool account,
        string artifactRoot)
    {
        if (account) return RevenantAdvisoryWorker.ScoreAnomaly(series, lineage, pin, artifactRoot, SsaAdvisory.Policy);
        AdvisoryEvidence Unavailable(AdvisoryStatus status) => new(series, lineage, pin, SsaAdvisory.Policy, status);

        try
        {
            const string kind = "forecast";
            var slash = pin.ModelId.IndexOf('/');
            if (slash <= 0 || pin.ModelId[..slash] != $"revenant-ssa-{kind}"
                || !ObservedFinanceEncoding.IsHash(pin.ModelId[(slash + 1)..]))
                return Unavailable(AdvisoryStatus.ArtifactInvalid);
            var directory = Path.Combine(artifactRoot, kind, pin.ModelId[(slash + 1)..]);
            var manifestBytes = File.ReadAllBytes(Path.Combine(directory, "manifest.json"));
            if (ObservationEncoding.Hash(manifestBytes) != pin.ModelId[(slash + 1)..])
                return Unavailable(AdvisoryStatus.ArtifactInvalid);
            var manifest = JsonSerializer.Deserialize<SsaAdvisoryManifest>(manifestBytes, RevenantAdvisoryWorker.ManifestJson);
            if (manifest is null || !manifest.IsValidFor(kind) || manifest.ArtifactSha256 != pin.ArtifactSha256)
                return Unavailable(AdvisoryStatus.ArtifactInvalid);
            var artifact = File.ReadAllBytes(Path.Combine(directory, "forecast.zip"));
            if (ObservationEncoding.Hash(artifact) != pin.ArtifactSha256)
                return Unavailable(AdvisoryStatus.ArtifactInvalid);
            var values = series.Points.Select(p => p.Value).ToArray();
            var ml = new MLContext(seed: 7);
            var forecastSpec = manifest.Forecast!;
            var reference = series.ReferenceMinor!.Value;
            if (values.Length <= forecastSpec.Warmup) return Unavailable(AdvisoryStatus.InsufficientHistory);
            var step = RevenantSsaInference.ForecastBank(ml, artifact, forecastSpec, manifest.Interval, reference, values)[^1];
            if (step.Bucket != values.Length - 1) throw new InvalidDataException("Forecast output is not at the requested seal.");
            var floor = reference / 10;
            var path = Enumerable.Range(0, V4BankTargets.HorizonCount).Select(h => new AdvisoryForecastStep(
                checked(series.RequestedBoundaryMs + (h + 1) * ObservedFinanceVersion.BucketMs), step.Central[h], step.NativeLower[h],
                step.NativeUpper[h], step.CalibratedLower[h], step.CalibratedUpper[h])).ToArray();
            var first = Array.FindIndex(path, p => p.CentralMinor < floor);
            int? crossing = values[^1] >= floor && first >= 0 ? first + 1 : null;
            return new(series, lineage, pin, SsaAdvisory.Policy, AdvisoryStatus.Available, ForecastV2: new(forecastSpec.Representation, reference,
                SsaAdvisory.Scale(reference, forecastSpec.ScaleDivisor), step.Sigma, floor, crossing, path));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Unavailable(AdvisoryStatus.ArtifactMissing);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or DllNotFoundException or BadImageFormatException)
        {
            return Unavailable(AdvisoryStatus.RuntimeUnavailable);
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or InvalidOperationException
            or OverflowException or NotSupportedException)
        {
            return Unavailable(AdvisoryStatus.ArtifactInvalid);
        }
    }
}
