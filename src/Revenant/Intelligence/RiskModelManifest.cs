using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Revenant.Intelligence;

/// <summary>Fixed trainer configuration. Part of the manifest; changing any value is a new model, never an edit of an old one.</summary>
public sealed record RiskTrainerOptions
{
    public string Trainer { get; init; } = "Microsoft.ML FastTreeBinaryTrainer (ML.NET FastTree, binary classification, built-in Platt calibration)";

    public int Seed { get; init; } = 8201;

    public int NumberOfThreads { get; init; } = 1;

    public int NumberOfLeaves { get; init; } = 8;

    public int NumberOfTrees { get; init; } = 100;

    public int MinimumExampleCountPerLeaf { get; init; } = 10;

    public double LearningRate { get; init; } = 0.1;

    /// <summary>No class weights, no oversampling, no hyper-parameter search: these are the first and only values tried.</summary>
    public string Notes { get; init; } = "Defaults for all other options; no tuning on validation or test; no class weights.";
}

/// <summary>Where and how the artifact was produced. Informational except <see cref="MlNetPackageVersion"/>, which a loader enforces.</summary>
public sealed record RiskModelEnvironment(
    string MlNetPackageVersion,
    string FastTreePackageVersion,
    string FrameworkDescription,
    string OsDescription,
    string ProcessArchitecture,
    string RuntimeIdentifier);

public sealed record RiskReferencePrediction(string Currency, long AmountMinor, long SourcePostedBeforeMinor, string FeatureHash, int ScorePpm, bool HoldAtThreshold);

public sealed record RiskModelTimings(double EtlSeconds, double TrainSeconds, double SaveLoadSeconds, double EvaluationSeconds, double InferenceMicrosecondsPerExample);

public sealed record RiskEvaluationReport(
    string CohortLabel,
    RiskMetrics TrainInSample,
    RiskMetrics Validation,
    RiskMetrics HeldOutTest,
    RiskMetrics[] Baselines,
    string BaselineNote,
    string[] Limitations);

/// <summary>
/// The immutable manifest of one trained artifact (Spec 08 R08.01). <see cref="ManifestSha256"/> covers every other field; evidence
/// pins that hash, so any edit to the manifest is a different identity. It records measured facts only.
/// </summary>
public sealed record RiskModelManifest(
    string ManifestSchema,
    string ModelId,
    string ModelKind,
    string CohortLabel,
    string ArtifactFileName,
    string ArtifactSha256,
    long ArtifactBytes,
    string FeatureSchemaId,
    string[] FeatureNames,
    string ScoreNormalizationId,
    RiskDatasetManifest Dataset,
    RiskTrainerOptions Trainer,
    RiskModelEnvironment Environment,
    RiskThresholdSelection ThresholdSelection,
    int ThresholdPpm,
    RiskEvaluationReport Evaluation,
    long ReproducibilityRowsChecked,
    bool LoadedEqualsFittedOnAllRows,
    RiskReferencePrediction[] ReferencePredictions,
    RiskModelTimings Timings,
    string TrainedAtUtc,
    string[] Limitations,
    string ManifestSha256)
{
    public const string Schema = "risk-model-manifest/1";
    public const string ModelIdFixture = "revenant-risk-fasttree-fixture/1";
}

/// <summary>Canonical JSON and hash for <see cref="RiskModelManifest"/>.</summary>
public static class RiskModelManifestCodec
{
    public static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Returns the manifest with <see cref="RiskModelManifest.ManifestSha256"/> recomputed over all other fields.</summary>
    public static RiskModelManifest Seal(RiskModelManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest with { ManifestSha256 = HashOf(manifest) };
    }

    public static bool HashMatches(RiskModelManifest manifest) =>
        string.Equals(manifest.ManifestSha256, HashOf(manifest), StringComparison.Ordinal);

    public static string ToJson(RiskModelManifest manifest) =>
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });

    private static string HashOf(RiskModelManifest manifest) =>
        Sha256Hex(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest with { ManifestSha256 = string.Empty })));
}
