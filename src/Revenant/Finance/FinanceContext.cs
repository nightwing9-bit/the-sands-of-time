using Marrow.Kernel.Canonical;
using Marrow.Kernel.Results;
using Revenant.Contracts;
using Revenant.Scenario;

namespace Revenant.Finance;

/// <summary>
/// Pinned context for one branch segment: scenario version and manifest hash, input and feature schema,
/// reducer version and the exact policy. Built only through <see cref="Pin"/>, which fails on any mismatch
/// instead of falling back to current defaults.
/// </summary>
public sealed record FinanceContext
{
    private FinanceContext(
        string scenarioVersion,
        string manifestContentHash,
        string inputSchemaVersion,
        string featureSchemaVersion,
        string reducerVersion,
        PolicyRef policyRef,
        PolicyConfig policy)
    {
        ScenarioVersion = scenarioVersion;
        ManifestContentHash = manifestContentHash;
        InputSchemaVersion = inputSchemaVersion;
        FeatureSchemaVersion = featureSchemaVersion;
        ReducerVersion = reducerVersion;
        PolicyRef = policyRef;
        Policy = policy;
    }

    public string ScenarioVersion { get; }
    public string ManifestContentHash { get; }
    public string InputSchemaVersion { get; }
    public string FeatureSchemaVersion { get; }
    public string ReducerVersion { get; }
    public PolicyRef PolicyRef { get; }
    public PolicyConfig Policy { get; }

    public static Result<FinanceContext, FinanceError> Pin(
        ScenarioManifest manifest,
        PolicyRef policy,
        string reducerVersion)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(policy);
        if (!ScenarioManifestCodec.HasValidContentHash(manifest))
        {
            return Mismatch("Scenario manifest content hash does not match its content.");
        }
        return FromPins(
            manifest.ScenarioVersion,
            manifest.ContentHash,
            manifest.InputSchemaVersion,
            manifest.FeatureSchemaVersion,
            reducerVersion,
            policy);
    }

    /// <summary>
    /// Rebuilds a context from previously pinned values, as stored durably by the Timeline (cold restore).
    /// Applies the same exact checks as <see cref="Pin"/> except the manifest content check, which needs the
    /// manifest itself; the reducer re-checks <see cref="ManifestContentHash"/> against the state on every step.
    /// </summary>
    public static Result<FinanceContext, FinanceError> FromPins(
        string scenarioVersion,
        string manifestContentHash,
        string inputSchemaVersion,
        string featureSchemaVersion,
        string reducerVersion,
        PolicyRef policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (string.IsNullOrWhiteSpace(manifestContentHash))
        {
            return Mismatch("Manifest content hash must be pinned.");
        }
        if (!string.Equals(scenarioVersion, ScenarioSchemas.FridayRingVersion, StringComparison.Ordinal))
        {
            return Mismatch($"Scenario version '{scenarioVersion}' is not supported by this reducer.");
        }
        if (!string.Equals(inputSchemaVersion, ScenarioSchemas.InputSchemaVersion, StringComparison.Ordinal))
        {
            return Mismatch($"Input schema '{inputSchemaVersion}' is not supported by this reducer.");
        }
        if (!string.Equals(reducerVersion, FinanceReducer.Version, StringComparison.Ordinal))
        {
            return Mismatch($"Reducer version '{reducerVersion}' is not '{FinanceReducer.Version}'.");
        }
        var resolved = PolicyCatalog.Resolve(policy);
        if (!resolved.TryGetValue(out var config))
        {
            return Result<FinanceContext, FinanceError>.Failure(resolved.Error);
        }
        if (!string.Equals(config.FeatureSchemaVersion, featureSchemaVersion, StringComparison.Ordinal))
        {
            return Mismatch(
                $"Policy feature schema '{config.FeatureSchemaVersion}' differs from scenario '{featureSchemaVersion}'.");
        }
        return Result<FinanceContext, FinanceError>.Success(new FinanceContext(
            scenarioVersion,
            manifestContentHash,
            inputSchemaVersion,
            featureSchemaVersion,
            reducerVersion,
            policy,
            config));
    }

    /// <summary>Canonical bytes of the pins; the policy is represented by its full <see cref="PolicyRef"/>.</summary>
    public byte[] EncodeCanonical() => new CanonicalWriter()
        .WriteInt32(1)
        .WriteString(ScenarioVersion)
        .WriteString(ManifestContentHash)
        .WriteString(InputSchemaVersion)
        .WriteString(FeatureSchemaVersion)
        .WriteString(ReducerVersion)
        .WriteString(PolicyRef.PolicyId)
        .WriteString(PolicyRef.ConfigHash)
        .WriteString(PolicyRef.ModelArtifactHash)
        .WriteString(PolicyRef.FeatureSchemaHash)
        .ToArray();

    public string ContentHash => ScenarioManifestCodec.Sha256Hex(EncodeCanonical());

    private static Result<FinanceContext, FinanceError> Mismatch(string message) =>
        FinanceError.Fail<FinanceContext>(FinanceErrorCode.VersionMismatch, message);
}
