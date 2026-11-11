using System.Reflection;
using System.Text.Json;
using Marrow.Kernel.Results;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers.FastTree;

namespace Revenant.Intelligence;

public enum RiskModelError
{
    ManifestMissing = 1,
    ManifestMalformed = 2,
    ManifestHashMismatch = 3,
    ArtifactMissing = 4,
    ArtifactChecksumMismatch = 5,
    FeatureSchemaIncompatible = 6,
    ModelSchemaIncompatible = 7,
    PackageVersionMismatch = 8,
    ModelLoadFailed = 9,
    ScoreNotFinite = 10,
    ScoreOutOfRange = 11,

    /// <summary>The artifact verified, but its identity is not the one a run's pinned risk policy names (task 8.3).</summary>
    PinnedIdentityMismatch = 12,

    /// <summary>The pre-screening facts cannot form a <c>risk-features-v1</c> vector (task 8.3); no screening is committed.</summary>
    FeaturesIncompatible = 13,
}

/// <summary>A typed load or scoring failure. A caller must not screen on failure (R08.08).</summary>
public sealed record RiskModelFailure(RiskModelError Code, string Detail);

/// <summary>The pinned identity a future Worker copies into <see cref="InferenceEvidenceV1"/>.</summary>
public sealed record RiskModelIdentity(
    string ModelId,
    string ArtifactSha256,
    string ManifestSha256,
    string MlNetPackageVersion,
    string FeatureSchemaId,
    int ThresholdPpm);

/// <summary>ML.NET row type shared by training and scoring. <c>Label</c> is a required schema column and is ignored when scoring.</summary>
public sealed class RiskModelInput
{
    public bool Label { get; set; }

    [VectorType(4)]
    public float[] Features { get; set; } = [];
}

internal static class MlNetVersions
{
    public static string MlNet => Of(typeof(MLContext));

    public static string FastTree => Of(typeof(FastTreeBinaryTrainer));

    private static string Of(Type type) =>
        (type.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? type.Assembly.GetName().Version?.ToString() ?? "unknown")
        .Split('+')[0];
}

/// <summary>
/// Loads one immutable, checksummed risk-classifier artifact and scores <see cref="RiskFeatureVector"/>s into integer PPM.
/// Verification order: manifest JSON, manifest self-hash, feature contract, artifact bytes SHA-256, package version, ML.NET model
/// schema. The artifact bytes are hashed and then loaded FROM THE SAME BYTES (no re-read), and ML.NET never sees a ZIP whose hash
/// differs from the manifest.
/// <para>
/// Scoring uses <c>ITransformer.Transform</c> on a fresh in-memory view per call: there is no shared mutable <c>PredictionEngine</c>,
/// so concurrent calls are safe. It does not train, download or write anything, and it knows nothing about the ledger.
/// </para>
/// </summary>
public sealed class RiskModelScorer
{
    public const string ArtifactFileName = "model.zip";
    public const string ManifestFileName = "manifest.json";
    public const string ProbabilityColumn = "Probability";

    private readonly RiskModelCore _core;

    private RiskModelScorer(RiskModelCore core, RiskModelManifest manifest)
    {
        _core = core;
        Manifest = manifest;
        Identity = new RiskModelIdentity(
            manifest.ModelId, manifest.ArtifactSha256, manifest.ManifestSha256, manifest.Environment.MlNetPackageVersion, manifest.FeatureSchemaId, manifest.ThresholdPpm);
    }

    public RiskModelManifest Manifest { get; }

    public RiskModelIdentity Identity { get; }

    public int ThresholdPpm => Manifest.ThresholdPpm;

    /// <summary>Loads <c>manifest.json</c> and <c>model.zip</c> from <paramref name="directory"/>.</summary>
    public static Result<RiskModelScorer, RiskModelFailure> LoadDirectory(string directory) =>
        Load(Path.Combine(directory, ManifestFileName), Path.Combine(directory, ArtifactFileName));

    public static Result<RiskModelScorer, RiskModelFailure> Load(string manifestPath, string artifactPath)
    {
        if (!File.Exists(manifestPath))
        {
            return Fail(RiskModelError.ManifestMissing, manifestPath);
        }
        RiskModelManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<RiskModelManifest>(File.ReadAllText(manifestPath));
        }
        catch (JsonException ex)
        {
            return Fail(RiskModelError.ManifestMalformed, ex.Message);
        }
        if (manifest is null || manifest.FeatureNames is null || manifest.Dataset is null || manifest.Environment is null || manifest.ModelId is null)
        {
            return Fail(RiskModelError.ManifestMalformed, "required members are missing");
        }
        if (manifest.ManifestSchema != RiskModelManifest.Schema)
        {
            return Fail(RiskModelError.ManifestMalformed, $"unknown manifest schema '{manifest.ManifestSchema}'");
        }
        if (!RiskModelManifestCodec.HashMatches(manifest))
        {
            return Fail(RiskModelError.ManifestHashMismatch, "the manifest content does not match its recorded SHA-256");
        }

        var violations = RiskFeaturesV1.ValidateModelInputColumns(manifest.FeatureNames);
        if (manifest.FeatureSchemaId != RiskFeaturesV1.SchemaId
            || manifest.ScoreNormalizationId != RiskScoreNormalization.NormalizationId
            || violations.Count > 0)
        {
            return Fail(RiskModelError.FeatureSchemaIncompatible,
                $"manifest declares feature schema '{manifest.FeatureSchemaId}' / normalization '{manifest.ScoreNormalizationId}'; {string.Join("; ", violations)}");
        }
        if (manifest.ThresholdPpm < 1 || manifest.ThresholdPpm > RiskScoreNormalization.PpmScale)
        {
            return Fail(RiskModelError.ManifestMalformed, "thresholdPpm is outside [1, 1000000]");
        }

        if (!File.Exists(artifactPath))
        {
            return Fail(RiskModelError.ArtifactMissing, artifactPath);
        }
        var bytes = File.ReadAllBytes(artifactPath);
        if (bytes.LongLength != manifest.ArtifactBytes || !string.Equals(RiskModelManifestCodec.Sha256Hex(bytes), manifest.ArtifactSha256, StringComparison.Ordinal))
        {
            return Fail(RiskModelError.ArtifactChecksumMismatch, "the artifact bytes do not match the manifest size/SHA-256");
        }
        if (!string.Equals(manifest.Environment.MlNetPackageVersion, MlNetVersions.MlNet, StringComparison.Ordinal))
        {
            return Fail(RiskModelError.PackageVersionMismatch, $"artifact pinned to ML.NET {manifest.Environment.MlNetPackageVersion}, runtime has {MlNetVersions.MlNet}");
        }

        var core = RiskModelCore.TryCreate(bytes, manifest.Trainer.Seed, out var failure);
        return core is not null
            ? Result<RiskModelScorer, RiskModelFailure>.Success(new RiskModelScorer(core, manifest))
            : Result<RiskModelScorer, RiskModelFailure>.Failure(failure!);
    }

    /// <summary>Scores one vector into PPM. See <see cref="ScoreBatch"/>.</summary>
    public Result<int, RiskModelFailure> Score(RiskFeatureVector vector) => _core.Score(vector);

    /// <summary>Scores vectors into integer PPM (exact round-half-away-from-zero of the calibrated probability). Fails on any non-finite or out-of-range probability.</summary>
    public Result<int[], RiskModelFailure> ScoreBatch(IReadOnlyList<RiskFeatureVector> vectors) => _core.ScoreBatch(vectors);

    /// <summary>The raw calibrated probabilities (floats), for diagnostics and tests only. Decisions use <see cref="Score"/>.</summary>
    public float[] Probabilities(IReadOnlyList<RiskFeatureVector> vectors) => _core.Probabilities(vectors);

    private static Result<RiskModelScorer, RiskModelFailure> Fail(RiskModelError code, string detail) =>
        Result<RiskModelScorer, RiskModelFailure>.Failure(new RiskModelFailure(code, detail));
}

/// <summary>
/// The verified-bytes-in, probabilities-out part of loading. Shared by <see cref="RiskModelScorer"/> (after manifest and checksum
/// verification) and by the offline trainer (which needs scores to choose the threshold before the manifest can be sealed).
/// </summary>
internal sealed class RiskModelCore
{
    private readonly MLContext _ml;
    private readonly ITransformer _model;

    private RiskModelCore(MLContext ml, ITransformer model)
    {
        _ml = ml;
        _model = model;
    }

    public static RiskModelCore? TryCreate(byte[] artifactBytes, int seed, out RiskModelFailure? failure)
    {
        var ml = new MLContext(seed: seed);
        ITransformer model;
        DataViewSchema inputSchema;
        try
        {
            using var stream = new MemoryStream(artifactBytes, writable: false);
            model = ml.Model.Load(stream, out inputSchema);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failure = new RiskModelFailure(RiskModelError.ModelLoadFailed, ex.Message);
            return null;
        }
        var problem = CheckSchema(model, inputSchema);
        if (problem is not null)
        {
            failure = new RiskModelFailure(RiskModelError.ModelSchemaIncompatible, problem);
            return null;
        }
        failure = null;
        return new RiskModelCore(ml, model);
    }

    public Result<int, RiskModelFailure> Score(RiskFeatureVector vector)
    {
        var batch = ScoreBatch([vector]);
        return batch.IsSuccess ? Result<int, RiskModelFailure>.Success(batch.Value[0]) : Result<int, RiskModelFailure>.Failure(batch.Error);
    }

    public Result<int[], RiskModelFailure> ScoreBatch(IReadOnlyList<RiskFeatureVector> vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        var probabilities = Probabilities(vectors);
        var ppm = new int[probabilities.Length];
        for (var i = 0; i < probabilities.Length; i++)
        {
            var converted = RiskScoreNormalization.FromProbability(probabilities[i]);
            if (converted.IsFailure)
            {
                var code = converted.Error == PpmError.NotFinite ? RiskModelError.ScoreNotFinite : RiskModelError.ScoreOutOfRange;
                return Result<int[], RiskModelFailure>.Failure(new RiskModelFailure(code, $"row {i}: probability {probabilities[i]}"));
            }
            ppm[i] = converted.Value;
        }
        return Result<int[], RiskModelFailure>.Success(ppm);
    }

    public float[] Probabilities(IReadOnlyList<RiskFeatureVector> vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        if (vectors.Count == 0)
        {
            return [];
        }
        var data = _ml.Data.LoadFromEnumerable(vectors.Select(v => new RiskModelInput { Features = v.ToArray() }).ToList());
        return _model.Transform(data).GetColumn<float>(RiskModelScorer.ProbabilityColumn).ToArray();
    }

    private static string? CheckSchema(ITransformer model, DataViewSchema input)
    {
        var features = input.GetColumnOrNull("Features");
        if (features is null
            || features.Value.Type is not VectorDataViewType { Size: RiskFeaturesV1.FeatureCount } vector
            || vector.ItemType != NumberDataViewType.Single)
        {
            return $"the model input must have a 'Features' column of Single[{RiskFeaturesV1.FeatureCount}]; found {features?.Type.ToString() ?? "none"}";
        }
        if (input.GetColumnOrNull("Label") is null)
        {
            return "the model input has no 'Label' column";
        }
        var output = model.GetOutputSchema(input).GetColumnOrNull(RiskModelScorer.ProbabilityColumn);
        return output is null || output.Value.Type != NumberDataViewType.Single
            ? $"the model has no Single '{RiskModelScorer.ProbabilityColumn}' output (a calibrated probability is required)"
            : null;
    }
}
