using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Marrow.Kernel.Canonical;
using Marrow.Kernel.Results;
using Microsoft.ML;
using Microsoft.ML.Data;
using Revenant.Observations;
using Revenant.Scenario;
using Revenant.Timeline;

namespace Revenant.Intelligence;

public enum BehavioralRiskErrorV2
{
    MissingManifest, InvalidManifest, InvalidPin, IncompatibleSchema, UnsupportedRuntime, MissingArtifact,
    InvalidArtifact, InvalidVector, InvalidScore, ReferenceMismatch,
}

public sealed record BehavioralRiskFailureV2(BehavioralRiskErrorV2 Code, string Detail);
public sealed record BehavioralRiskScoreV2(string FeatureDigest, float RawScore, int RiskPpm, bool AtOrAboveThreshold);

internal sealed record RiskArtifactReferenceV2(string Case, byte[] Vector, string VectorSha256, float RawScore, int RiskPpm, bool AtOrAboveThreshold);
internal sealed record RiskArtifactIdentityV2(string FeatureSchema, string FeatureSchemaSha256, string[] OrderedFeatures,
    string Preprocessing, string Generator, string DatasetSha256, string[] OrderedSplitSha256, int TrainingSeed,
    string Trainer, int Trees, int Leaves, int MinLeaf, double LearningRate, int Threads, string Runtime,
    string CalibrationMethod, double Slope, double Intercept, int ThresholdPpm, string ScoreSemantics,
    string ReviewPolicy, string SelectionRevision, string FreezeSha256, string EvaluationSha256, string ChallengeSha256,
    string ArtifactSha256, RiskArtifactReferenceV2[] References);
internal sealed record RiskArtifactManifestV2(string Schema, string ModelId, RiskArtifactIdentityV2 Identity)
{
    internal const string SchemaV2 = "fraud-model-manifest/2";
    internal const string Runtime = "ML.NET 5.0.1; .NET 10; Linux x64";
    internal const string Preprocessing = "canonical-exact-single";
    internal const string Calibration = "calibration-root-monotonic-sigmoid-on-raw-score/1";
    internal const string ScoreSemantics = "Score/Single;sigmoid(slope*Score+intercept);probability-ppm/away-from-zero;inclusive-threshold";
    private static readonly JsonSerializerOptions ReadOptions = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    internal static RiskArtifactManifestV2 Seal(RiskArtifactIdentityV2 identity) =>
        new(SchemaV2, ModelIdOf(identity), identity);

    private static string ModelIdOf(RiskArtifactIdentityV2 identity) =>
        BehavioralRiskScorerV2.ArtifactDirectory + "/" + ObservationEncoding.Hash(JsonSerializer.SerializeToUtf8Bytes(identity));

    internal ObservedRiskPin Pin => new(ModelId, Identity.ArtifactSha256, Identity.ThresholdPpm);
    internal byte[] Encode() => JsonSerializer.SerializeToUtf8Bytes(this, WriteOptions);

    internal static RiskArtifactManifestV2 Decode(ReadOnlySpan<byte> bytes)
    {
        var manifest = JsonSerializer.Deserialize<RiskArtifactManifestV2>(bytes, ReadOptions);
        if (manifest is null || manifest.Identity is null || manifest.Schema != SchemaV2
            || !bytes.SequenceEqual(manifest.Encode()))
            throw new CanonicalFormatException("Expected the canonical v2 model manifest.");
        return manifest;
    }

    internal bool MatchesSchema() => Identity.FeatureSchema == RiskFeatureVectorV2.Schema
        && Identity.FeatureSchemaSha256 == RiskFeatureVectorV2.SchemaHash
        && Identity.OrderedFeatures is not null
        && Identity.OrderedFeatures.SequenceEqual(RiskFeatureVectorV2.Columns.Select(x => x.Name), StringComparer.Ordinal);

    internal bool ValidIdentity()
    {
        var m = Identity;
        return ModelId == ModelIdOf(m) && m.Runtime == Runtime && m.Preprocessing == Preprocessing
            && m.Generator == BehavioralEpisodeScenario.GeneratorVersionV2 && m.ThresholdPpm is > 0 and <= 1_000_000
            && m.CalibrationMethod == Calibration && m.ScoreSemantics == ScoreSemantics
            && double.IsFinite(m.Slope) && m.Slope > 0 && double.IsFinite(m.Intercept)
            && m.Trainer == "fasttree-8x60" && m.Trees == 60 && m.Leaves == 8 && m.MinLeaf == 18
            && m.LearningRate == .08 && m.Threads == 1 && m.TrainingSeed == 17081129
            && !string.IsNullOrWhiteSpace(m.ReviewPolicy)
            && m.SelectionRevision is { Length: 40 } revision && revision.All(char.IsAsciiHexDigitLower)
            && m.OrderedSplitSha256 is { Length: 4 } && m.OrderedSplitSha256.All(ObservedFinanceEncoding.IsHash)
            && new[] { m.ArtifactSha256, m.DatasetSha256, m.FreezeSha256, m.EvaluationSha256, m.ChallengeSha256 }
                .All(ObservedFinanceEncoding.IsHash)
            && m.References is { Length: 7 } && m.References.All(r => r is not null && r.Vector is { Length: > 0 }
                && !string.IsNullOrWhiteSpace(r.Case) && ObservedFinanceEncoding.IsHash(r.VectorSha256)
                && float.IsFinite(r.RawScore) && r.RiskPpm is >= 0 and <= 1_000_000)
            && m.References.Select(r => r.Case).Distinct(StringComparer.Ordinal).Count() == m.References.Length;
    }
}

/// <summary>Read-only scorer for an exact V4 ModelRequired pin. Each call consumes only canonical risk-features-v2 bytes.</summary>
public sealed class BehavioralRiskScorerV2
{
    public const string ArtifactDirectory = "revenant-behavioral-risk-v2";
    public const string FeatureSchema = RiskFeatureVectorV2.Schema;
    public static ObservedRiskPin PublishedPin { get; } = new(
        "revenant-behavioral-risk-v2/d1801a3f72f9d4525cd39b8b7873e2fc43288dea0f7c1f5ee8646612833a28e4",
        "56bca32c36dd1911d163a6f92369da43382e1937d875228339f15f8608519c7c", 852469);
    internal const string ManifestFile = "manifest.json";
    internal const string ModelFile = "model.zip";
    private readonly RiskPipelineV2 pipeline;
    private readonly RiskArtifactManifestV2 manifest;

    private BehavioralRiskScorerV2(RiskPipelineV2 pipeline, RiskArtifactManifestV2 manifest) =>
        (this.pipeline, this.manifest) = (pipeline, manifest);

    public ObservedRiskPin Pin => manifest.Pin;
    public int VerifiedReferenceCount => manifest.Identity.References.Length;

    public static Result<BehavioralRiskScorerV2, BehavioralRiskFailureV2> Load(
        string artifactDirectory, ObservedFinanceContext context) =>
        context.RiskMode == ObservedRiskMode.ModelRequired && context.RiskModel is { } pin
            ? Load(artifactDirectory, context.RiskFeatureSchema, pin)
            : LoadFailure(BehavioralRiskErrorV2.InvalidPin, "Expected an explicit V4 ModelRequired context.");

    public static Result<BehavioralRiskScorerV2, BehavioralRiskFailureV2> Load(
        string artifactDirectory, string featureSchema, ObservedRiskPin pin)
    {
        if (featureSchema != FeatureSchema)
            return LoadFailure(BehavioralRiskErrorV2.IncompatibleSchema, "V4 feature schema pin does not match.");
        if (pin != PublishedPin)
            return LoadFailure(BehavioralRiskErrorV2.InvalidPin, "No registered artifact matches the supplied V4 risk pin.");
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.ProcessArchitecture != Architecture.X64
            || Environment.Version.Major != 10)
            return LoadFailure(BehavioralRiskErrorV2.UnsupportedRuntime, "The qualified runtime is .NET 10 on Linux AMD64.");
        RiskArtifactManifestV2 manifest;
        try
        {
            manifest = RiskArtifactManifestV2.Decode(File.ReadAllBytes(Path.Combine(artifactDirectory, ManifestFile)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return LoadFailure(BehavioralRiskErrorV2.MissingManifest, "The pinned model manifest cannot be read.");
        }
        catch (Exception e) when (e is JsonException or CanonicalFormatException)
        {
            return LoadFailure(BehavioralRiskErrorV2.InvalidManifest, "Invalid canonical model manifest.");
        }

        if (!manifest.MatchesSchema())
            return LoadFailure(BehavioralRiskErrorV2.IncompatibleSchema, "Manifest feature digest, count or order does not match.");
        if (!manifest.ValidIdentity() || manifest.Pin != pin)
            return LoadFailure(BehavioralRiskErrorV2.InvalidPin, "Model identity, calibrator, artifact hash or threshold does not match.");

        byte[] zip;
        try { zip = File.ReadAllBytes(Path.Combine(artifactDirectory, ModelFile)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return LoadFailure(BehavioralRiskErrorV2.MissingArtifact, "The pinned model ZIP cannot be read.");
        }
        if (ObservationEncoding.Hash(zip) != pin.ArtifactSha256)
            return LoadFailure(BehavioralRiskErrorV2.InvalidArtifact, "Model ZIP digest does not match.");

        BehavioralRiskScorerV2 scorer;
        try { scorer = new(RiskPipelineV2.Load(zip), manifest); }
        catch (Exception e) when (e is InvalidOperationException or FormatException or IOException or ArgumentException or NotSupportedException)
        {
            return LoadFailure(BehavioralRiskErrorV2.InvalidArtifact, "The pinned ML.NET pipeline could not be loaded.");
        }

        foreach (var reference in manifest.Identity.References)
        {
            var scored = scorer.Score(reference.Vector);
            if (scored.IsFailure || scored.Value.FeatureDigest != reference.VectorSha256 || scored.Value.RawScore != reference.RawScore
                || scored.Value.RiskPpm != reference.RiskPpm || scored.Value.AtOrAboveThreshold != reference.AtOrAboveThreshold)
                return LoadFailure(BehavioralRiskErrorV2.ReferenceMismatch, $"Reference scoring failed for {reference.Case}.");
        }
        return Result<BehavioralRiskScorerV2, BehavioralRiskFailureV2>.Success(scorer);
    }

    public Result<BehavioralRiskScoreV2, BehavioralRiskFailureV2> Score(ReadOnlySpan<byte> canonicalVector)
    {
        try { return Score(RiskFeatureVectorV2.Decode(canonicalVector)); }
        catch (CanonicalFormatException)
        {
            return Result<BehavioralRiskScoreV2, BehavioralRiskFailureV2>.Failure(new(BehavioralRiskErrorV2.InvalidVector,
                "The canonical feature bytes do not match risk-features-v2."));
        }
    }

    internal Result<BehavioralRiskScoreV2, BehavioralRiskFailureV2> Score(RiskFeatureVectorV2 vector)
    {
        try
        {
            var raw = pipeline.RawScore(vector);
            var m = manifest.Identity;
            var ppm = RiskProbabilityV2.Ppm(RiskProbabilityV2.Sigmoid(m.Slope * raw + m.Intercept));
            return Result<BehavioralRiskScoreV2, BehavioralRiskFailureV2>.Success(new(vector.Digest, raw, ppm, ppm >= m.ThresholdPpm));
        }
        catch (Exception e) when (e is InvalidOperationException or ArithmeticException or ArgumentException or FormatException)
        {
            return Result<BehavioralRiskScoreV2, BehavioralRiskFailureV2>.Failure(new(BehavioralRiskErrorV2.InvalidScore,
                "The pinned scoring pipeline did not produce a valid risk score."));
        }
    }

    private static Result<BehavioralRiskScorerV2, BehavioralRiskFailureV2> LoadFailure(BehavioralRiskErrorV2 code, string detail) =>
        Result<BehavioralRiskScorerV2, BehavioralRiskFailureV2>.Failure(new(code, detail));
}

internal sealed class RiskPipelineV2(MLContext context, ITransformer model)
{
    private readonly object gate = new();

    internal static RiskPipelineV2 Load(byte[] zip)
    {
        var ml = new MLContext(seed: 17081129);
        using var stream = new MemoryStream(zip, writable: false);
        var fitted = ml.Model.Load(stream, out var schema);
        if (schema.GetColumnOrNull("Features")?.Type is not VectorDataViewType { Size: 70 } type
            || type.ItemType != NumberDataViewType.Single)
            throw new InvalidOperationException("Unexpected model input vector type.");
        return new(ml, fitted);
    }

    internal float RawScore(RiskFeatureVectorV2 vector)
    {
        // Transform an isolated view and serialize access to the ML.NET objects; there is no shared PredictionEngine.
        lock (gate)
        {
            var view = context.Data.LoadFromEnumerable(new[] { new Input { Features = vector.ToModelInput() } });
            var value = model.Transform(view).GetColumn<float>("Score").Single();
            return float.IsFinite(value) ? value : throw new InvalidOperationException("Nonfinite raw score.");
        }
    }

    private sealed class Input
    {
        [VectorType(70)]
        public float[] Features { get; set; } = [];
    }
}

internal static class RiskProbabilityV2
{
    internal static double Sigmoid(double logit)
    {
        if (!double.IsFinite(logit)) throw new InvalidOperationException("Nonfinite calibration logit.");
        var exponential = Math.Exp(-Math.Abs(logit));
        return logit >= 0 ? 1 / (1 + exponential) : exponential / (1 + exponential);
    }

    internal static int Ppm(double probability)
    {
        if (!double.IsFinite(probability) || probability is < 0 or > 1)
            throw new InvalidOperationException("Invalid probability.");
        return (int)Math.Clamp(Math.Round(probability * 1_000_000, MidpointRounding.AwayFromZero), 0, 1_000_000);
    }
}
