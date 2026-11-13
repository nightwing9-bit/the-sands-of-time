using System.Collections.Immutable;
using System.Text.Json;
using Hosts.Worker;
using Revenant.Intelligence;
using Revenant.Observations;
using Xunit;

namespace Revenant.Tests.Intelligence.R7;

public sealed class R7OptionAArtifactTests
{
    [Fact]
    [Trait("Category", "R7Parity")]
    public void Published_anomaly_matches_G4_references_and_fails_explicitly_without_valid_artifact()
    {
        var pilot = Environment.GetEnvironmentVariable("REVENANT_R7_PILOT_INPUT")
            ?? throw new InvalidOperationException("Set REVENANT_R7_PILOT_INPUT to the retained G4 pilot.");
        var root = LongitudinalSample.Read(pilot, LongitudinalSample.ReadManifest(pilot, "pilot.json").Roots[2]);
        var pin = RevenantAdvisoryWorker.SupportedAnomaly;
        var id = pin.ModelId.Split('/')[1];
        var directory = Path.Combine(RevenantAdvisoryWorker.ArtifactRoot, "anomaly", id);
        Assert.Equal(id, ObservationEncoding.Hash(File.ReadAllBytes(Path.Combine(directory, "manifest.json"))));
        Assert.Equal(pin.ArtifactSha256, ObservationEncoding.Hash(File.ReadAllBytes(Path.Combine(directory, "anomaly.zip"))));
        using var report = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "development.json")));
        Assert.Equal(pin.ModelId, report.RootElement.GetProperty("ModelId").GetString());
        Assert.Equal(pin.ArtifactSha256, report.RootElement.GetProperty("ArtifactSha256").GetString());
        var threshold = report.RootElement.GetProperty("Threshold").GetDouble();
        var prefixes = new List<V4AdvisorySeries>();
        foreach (var item in report.RootElement.GetProperty("References").EnumerateArray())
        {
            var stream = root.Accounts.Single(a => a.SubjectId == item.GetProperty("Subject").GetString());
            Assert.Equal(stream.Digest, item.GetProperty("StreamSha256").GetString());
            var expected = item.GetProperty("Step").Deserialize<SsaAnomalyStep>();
            var prefix = Prefix(stream, expected.Bucket);
            Assert.Equal(item.GetProperty("BoundaryMs").GetInt64(), prefix.RequestedBoundaryMs);
            prefixes.Add(prefix);
            var evidence = Score(prefix, RevenantAdvisoryWorker.ArtifactRoot);
            Assert.Equal(AdvisoryStatus.Available, evidence.Status);
            var actual = Assert.IsType<AdvisoryAnomalyV2>(evidence.AnomalyV2);
            Assert.Equal(expected.Count, actual.Count);
            Assert.Equal(expected.ExpectedCount, actual.ExpectedCount);
            Assert.Equal(expected.Residual, actual.Residual);
            Assert.Equal(expected.Statistic, actual.StatisticValue);
            Assert.Equal(expected.Alert, actual.Alert);
            Assert.Equal(expected.NativeAlert, actual.NativeAlert);
            Assert.Equal(expected.NativePValue, actual.NativePValue);
            Assert.Equal(threshold, actual.Threshold);
            Assert.Equal("anomaly-evidence/2", evidence.Schema);
            Assert.Equal(AdvisoryEvidence.GlobalAnomalyPolicy, evidence.Policy);
            Assert.Equal(evidence.Encode(), Score(prefix, RevenantAdvisoryWorker.ArtifactRoot).Encode());
        }
        Assert.NotEmpty(prefixes);
        Assert.Equal(AdvisoryStatus.InsufficientHistory, Score(Prefix(prefixes[0], 1151), RevenantAdvisoryWorker.ArtifactRoot).Status);

        var temp = Directory.CreateTempSubdirectory("r7-option-a-").FullName;
        try
        {
            var prefix = prefixes[0];
            AssertUnavailable(AdvisoryStatus.ArtifactMissing, Score(prefix, temp));
            var copy = Directory.CreateDirectory(Path.Combine(temp, "anomaly", id)).FullName;
            var manifest = Path.Combine(copy, "manifest.json");
            File.WriteAllText(manifest, "{}");
            AssertUnavailable(AdvisoryStatus.ArtifactInvalid, Score(prefix, temp));
            File.Copy(Path.Combine(directory, "manifest.json"), manifest, overwrite: true);
            AssertUnavailable(AdvisoryStatus.ArtifactMissing, Score(prefix, temp));
            File.WriteAllBytes(Path.Combine(copy, "anomaly.zip"), [0, 1, 2]);
            AssertUnavailable(AdvisoryStatus.ArtifactInvalid, Score(prefix, temp));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    private static void AssertUnavailable(AdvisoryStatus expected, AdvisoryEvidence evidence)
    {
        var receipt = RevenantAdvisoryWorker.Describe(evidence, null);
        Assert.Equal(expected, receipt.Status);
        Assert.NotNull(receipt.CausalOrigin);
        Assert.Null(receipt.Anomaly);
        Assert.Null(receipt.Liquidity);
        Assert.Null(receipt.EvidenceSha256);
    }

    private static AdvisoryEvidence Score(V4AdvisorySeries series, string root) =>
        RevenantAdvisoryWorker.ScoreSupported(series, series.ExecutionRunId, root);

    private static V4AdvisorySeries Prefix(V4AdvisorySeries series, int index) => series with
    {
        Points = ImmutableArray.Create(series.Points.AsSpan(0, index + 1)),
        RequestedBoundaryMs = series.Points[index].Seal.BoundaryMs,
    };
}
