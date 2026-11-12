using System.Text.Json;
using Revenant.Intelligence;
using Revenant.Observations;
using Revenant.Scenario;
using Revenant.Synthetic;
using Revenant.Tests.Observations;
using Revenant.Timeline;
using Xunit;

namespace Revenant.Tests.Intelligence;

public sealed class R5ArtifactTests
{
    [Fact]
    public void R5_probability_quantization_rejects_invalid_outputs_and_rounds_away_from_zero()
    {
        Assert.Equal(1, RiskProbabilityV2.Ppm(.0000005));
        Assert.Equal(1_000_000, RiskProbabilityV2.Ppm(1));
        Assert.Equal(0, RiskProbabilityV2.Ppm(0));
        Assert.Throws<InvalidOperationException>(() => RiskProbabilityV2.Sigmoid(double.NaN));
        Assert.Throws<InvalidOperationException>(() => RiskProbabilityV2.Ppm(double.PositiveInfinity));
        Assert.Throws<InvalidOperationException>(() => RiskProbabilityV2.Ppm(-1));
    }

    [Fact]
    [Trait("Category", "OfflineTraining")]
    public void Seal_the_exact_independently_qualified_R5_ZIP_without_retraining()
    {
        var selection = Required("R5_CANDIDATE_INPUT");
        var freezeBytes = File.ReadAllBytes(Path.Combine(selection, "freeze.json"));
        var evaluationBytes = File.ReadAllBytes(Required("R5_EVALUATION_REPORT"));
        var freezeSha = Required("R5_FREEZE_SHA");
        var evaluationSha = Required("R5_EVALUATION_SHA");
        ObservationEncoding.RequireDigest(freezeBytes, freezeSha);
        ObservationEncoding.RequireDigest(evaluationBytes, evaluationSha);
        var freeze = JsonSerializer.Deserialize<R5Freeze>(freezeBytes)!;
        using var evaluation = JsonDocument.Parse(evaluationBytes);
        Assert.True(evaluation.RootElement.GetProperty("CandidateQualifiesForArtifactVerification").GetBoolean());
        Assert.Equal(freezeSha, evaluation.RootElement.GetProperty("FreezeSha256").GetString());
        var reader = new V4RiskDatasetReader(Required("R5_COHORT_INPUT"), freeze.Selection.DatasetSha256);
        var train = reader.ReadSplit(V4DatasetSplit.Train);
        var zip = File.ReadAllBytes(Path.Combine(selection, "candidate.zip"));
        ObservationEncoding.RequireDigest(zip, freeze.Selection.CandidateSha256);
        var pipeline = RiskPipelineV2.Load(zip);
        var references = new[]
        {
            Reference("ordinary-low-risk", train.First(r => !r.Truth.Malicious && !r.Truth.HardNegative &&
                r.AsOf.BeneficiarySeenBefore && r.AsOf.AmountMinor < 20_000).Vector),
            Reference("large-legitimate", train.Where(r => !r.Truth.Malicious).MaxBy(r => r.AsOf.AmountMinor)!.Vector),
            Reference("legitimate-new-beneficiary", train.First(r => !r.Truth.Malicious && !r.Truth.HardNegative &&
                r.AsOf.ReportedNewBeneficiary).Vector),
            Reference("behavioral-deviation", train.First(r => r.Truth.Attack == EpisodeAttack.Takeover).Vector),
            Reference("rapid-ring-forwarding", train.Where(r => r.Truth.Attack == EpisodeAttack.Ring)
                .MaxBy(r => r.Vector.Values[^2])!.Vector),
            Reference("missing-history", Edge(drain: false)),
            Reference("zero-available", Edge(drain: true)),
        };
        var identity = new RiskArtifactIdentityV2(RiskFeatureVectorV2.Schema, RiskFeatureVectorV2.SchemaHash,
            RiskFeatureVectorV2.Columns.Select(c => c.Name).ToArray(), freeze.Selection.Preprocessing,
            BehavioralEpisodeScenario.GeneratorVersionV2, freeze.Selection.DatasetSha256,
            new[] { "Train", "Validation", "Calibration", "FinalTest" }.Select(name => freeze.SplitSha256[name]).ToArray(),
            freeze.Selection.TrainingSeed, freeze.Selection.Config.Id, freeze.Selection.Config.Trees, freeze.Selection.Config.Leaves,
            freeze.Selection.Config.MinLeaf, .08, 1, freeze.Selection.Runtime, freeze.CalibrationMethod, freeze.Slope, freeze.Intercept,
            freeze.ThresholdPpm, RiskArtifactManifestV2.ScoreSemantics, freeze.ReviewPolicy, freeze.Selection.ExperimentRevision,
            freezeSha, evaluationSha, evaluation.RootElement.GetProperty("ChallengeDatasetSha256").GetString()!,
            freeze.Selection.CandidateSha256, references);
        var manifest = RiskArtifactManifestV2.Seal(identity);
        Assert.True(manifest.ValidIdentity());
        Assert.True(manifest.MatchesSchema());
        var directory = Required("R5_ARTIFACT_OUTPUT");
        Write(BehavioralRiskScorerV2.ModelFile, zip);
        Write(BehavioralRiskScorerV2.ManifestFile, manifest.Encode());
        Write("model.zip.sha256", System.Text.Encoding.ASCII.GetBytes(identity.ArtifactSha256 + "  model.zip\n"));

        RiskArtifactReferenceV2 Reference(string name, RiskFeatureVectorV2 vector)
        {
            var raw = pipeline.RawScore(vector);
            var ppm = RiskProbabilityV2.Ppm(RiskProbabilityV2.Sigmoid(freeze.Slope * raw + freeze.Intercept));
            return new(name, vector.Encode(), vector.Digest, raw, ppm, ppm >= freeze.ThresholdPpm);
        }

        void Write(string name, byte[] bytes)
        {
            using var stream = File.Open(Path.Combine(directory, name), FileMode.CreateNew);
            stream.Write(bytes);
        }
    }

    [Fact]
    [Trait("Category", "R5ArtifactX64")]
    public void R5_pinned_artifact_loads_and_scores_references_and_rejects_corruption_on_linux_x64()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Intelligence", "Artifacts", BehavioralRiskScorerV2.ArtifactDirectory);
        var manifest = RiskArtifactManifestV2.Decode(File.ReadAllBytes(Path.Combine(directory, BehavioralRiskScorerV2.ManifestFile)));
        var pin = manifest.Pin;
        Assert.Equal(BehavioralRiskScorerV2.PublishedPin, pin);
        Assert.Equal("56bca32c36dd1911d163a6f92369da43382e1937d875228339f15f8608519c7c", pin.ArtifactSha256);
        Assert.Equal(852469, pin.ThresholdPpm);
        var rulesOnly = new ObservationFixture().Context;
        var context = new ObservedFinanceContext(rulesOnly.ScenarioVersion, rulesOnly.ManifestSha256, rulesOnly.OriginMs,
            rulesOnly.BasePolicy, rulesOnly.ReviewPolicy, ObservedRiskMode.ModelRequired, RiskFeatureVectorV2.Schema, pin, [], []);
        Assert.Equal(BehavioralRiskErrorV2.InvalidPin, BehavioralRiskScorerV2.Load(directory, rulesOnly).Error.Code);
        var scorer = BehavioralRiskScorerV2.Load(directory, context).Value;
        Assert.Equal(pin, scorer.Pin);
        Assert.Equal(7, scorer.VerifiedReferenceCount);
        Parallel.For(0, 56, i =>
        {
            var reference = manifest.Identity.References[i % 7];
            var score = scorer.Score(reference.Vector).Value;
            Assert.Equal(reference.VectorSha256, score.FeatureDigest);
            Assert.Equal(reference.RawScore, score.RawScore);
            Assert.Equal(reference.RiskPpm, score.RiskPpm);
            Assert.Equal(reference.AtOrAboveThreshold, score.AtOrAboveThreshold);
        });
        Assert.Equal(BehavioralRiskErrorV2.InvalidVector, scorer.Score(manifest.Identity.References[0].Vector[..^1]).Error.Code);
        Assert.Equal(BehavioralRiskErrorV2.IncompatibleSchema, BehavioralRiskScorerV2.Load(directory, "risk-features-v1", pin).Error.Code);
        Assert.Equal(BehavioralRiskErrorV2.InvalidPin,
            BehavioralRiskScorerV2.Load(directory, RiskFeatureVectorV2.Schema, pin with { ThresholdPpm = pin.ThresholdPpm - 1 }).Error.Code);

        var scratch = Directory.CreateTempSubdirectory("r5-artifact-");
        try
        {
            var manifestPath = Path.Combine(scratch.FullName, BehavioralRiskScorerV2.ManifestFile);
            var modelPath = Path.Combine(scratch.FullName, BehavioralRiskScorerV2.ModelFile);
            File.WriteAllBytes(manifestPath, manifest.Encode());
            Assert.Equal(BehavioralRiskErrorV2.MissingArtifact, Load().Error.Code);
            File.WriteAllBytes(modelPath, [0, 1, 2]);
            Assert.Equal(BehavioralRiskErrorV2.InvalidArtifact, Load().Error.Code);

            var reordered = manifest.Identity.OrderedFeatures.Reverse().ToArray();
            File.WriteAllBytes(manifestPath, (manifest with { Identity = manifest.Identity with { OrderedFeatures = reordered } }).Encode());
            Assert.Equal(BehavioralRiskErrorV2.IncompatibleSchema, Load().Error.Code);

            File.WriteAllBytes(manifestPath, (manifest with
            {
                Identity = manifest.Identity with { Slope = manifest.Identity.Slope / 2 },
            }).Encode());
            Assert.Equal(BehavioralRiskErrorV2.InvalidPin, Load().Error.Code);
        }
        finally { scratch.Delete(recursive: true); }

        Marrow.Kernel.Results.Result<BehavioralRiskScorerV2, BehavioralRiskFailureV2> Load() =>
            BehavioralRiskScorerV2.Load(scratch.FullName, RiskFeatureVectorV2.Schema, pin);
    }

    private static RiskFeatureVectorV2 Edge(bool drain)
    {
        var fixture = new ObservationFixture();
        var at = fixture.Origin + 1;
        if (drain)
        {
            var p = fixture.Intent("drain-reference", at, fixture.Run.Manifest.Accounts[0].OpeningBalance.MinorUnits);
            fixture.Screen(p, at);
            fixture.Settle(p, at);
        }
        var payment = fixture.Intent("edge-reference", at);
        return V4ObservationProjector.RiskAt(fixture.History(), payment.IntentId, at).Vector;
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"Missing {name}.");
}
