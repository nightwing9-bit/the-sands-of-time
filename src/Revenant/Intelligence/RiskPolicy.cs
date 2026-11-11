using System.Collections.Concurrent;
using Marrow.Kernel.Results;
using Revenant.Finance;

namespace Revenant.Intelligence;

/// <summary>
/// A pinned model-aware screening policy (Spec 08 task 8.3, ADR-P1-20): the rules-only base policy whose hard outcomes keep
/// precedence, plus the exact identity (artifact and manifest SHA-256, ML.NET version, feature schema, threshold) of the one trained
/// classifier that may turn a base-clear payment into Held. <see cref="ArtifactDirectory"/> only says where the bytes are expected;
/// the identity is what is checked.
/// </summary>
public sealed record RiskPolicy(string PolicyId, PolicyRef BasePolicy, RiskModelIdentity Model, string ArtifactDirectory);

/// <summary>
/// Frozen model-aware risk policies. Resolution is exact on the id and every pinned field; there is no "latest" or default model.
/// A different artifact or threshold is a new policy id. A run's policy never changes after it is opened.
/// </summary>
public static class RiskPolicyCatalog
{
    public const string V1Id = "v7-base+ml-risk-v1";

    /// <summary>
    /// <c>v7-base+ml-risk-v1</c>: policy v7, then the committed fixture classifier <c>revenant-risk-fasttree-fixture/1</c>
    /// (task 8.2) at its validation-selected threshold. The pins are copied from the committed manifest and are asserted equal to the
    /// identity the verified loader reports for those bytes.
    /// </summary>
    public static RiskPolicy V1 { get; } = new(
        V1Id,
        PolicyCatalog.RefOf(PolicyCatalog.V7),
        new RiskModelIdentity(
            "revenant-risk-fasttree-fixture/1",
            "c13491fef4252e462165c84d1df5c861596638ca60eeb9e86fd020496aacd3de",
            "00431188ea8ac78cfa948ef2da78383f659b5c1ada5dec78e89171ac646c658a",
            "5.0.1",
            RiskFeaturesV1.SchemaId,
            257_796),
        "risk-fixture-fasttree-v1");

    public static Result<RiskPolicy, string> Resolve(string policyId, RiskModelIdentity model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var policy = policyId switch
        {
            V1Id => V1,
            _ => null,
        };
        if (policy is null)
        {
            return Result<RiskPolicy, string>.Failure($"Unknown risk policy '{policyId}'.");
        }
        return policy.Model == model
            ? Result<RiskPolicy, string>.Success(policy)
            : Result<RiskPolicy, string>.Failure($"Pinned model {model} does not match risk policy {policyId}'s {policy.Model}.");
    }
}

/// <summary>Settings of the pinned-artifact loader (<c>Revenant:RiskModels</c>).</summary>
public sealed class RiskModelOptions
{
    public const string SectionName = "Revenant:RiskModels";

    /// <summary>Directory holding one sub-directory per artifact. Default: the artifacts copied next to the Revenant assembly.</summary>
    public string? ArtifactRoot { get; set; }

    public static string DefaultArtifactRoot => Path.Combine(AppContext.BaseDirectory, "Intelligence", "Artifacts");
}

/// <summary>
/// Loads the trained artifact a pinned <see cref="RiskPolicy"/> names, once per process, through the verifying
/// <see cref="RiskModelScorer"/> loader (manifest self-hash, checksum, schema, package version), then requires the verified identity
/// to equal the policy's pins. A successful load is cached; a failure is returned typed and retried on the next call, so a later
/// deployment of the bytes is picked up without silently scoring with anything else. It never trains or downloads.
/// </summary>
public sealed class RiskModelProvider(string artifactRoot)
{
    private readonly ConcurrentDictionary<string, RiskModelScorer> _loaded = new(StringComparer.Ordinal);

    public string ArtifactRoot { get; } = artifactRoot ?? throw new ArgumentNullException(nameof(artifactRoot));

    public Result<RiskModelScorer, RiskModelFailure> Get(RiskPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (_loaded.TryGetValue(policy.PolicyId, out var cached))
        {
            return Result<RiskModelScorer, RiskModelFailure>.Success(cached);
        }
        var loaded = RiskModelScorer.LoadDirectory(Path.Combine(ArtifactRoot, policy.ArtifactDirectory));
        if (!loaded.TryGetValue(out var scorer))
        {
            return loaded;
        }
        if (scorer.Identity != policy.Model)
        {
            return Result<RiskModelScorer, RiskModelFailure>.Failure(new RiskModelFailure(RiskModelError.PinnedIdentityMismatch,
                $"verified artifact {scorer.Identity} is not the {policy.PolicyId} pin {policy.Model}"));
        }
        return Result<RiskModelScorer, RiskModelFailure>.Success(_loaded.GetOrAdd(policy.PolicyId, scorer));
    }
}
