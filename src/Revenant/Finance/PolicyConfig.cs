using System.Collections.Immutable;
using Marrow.Kernel.Canonical;
using Marrow.Kernel.Results;
using Revenant.Contracts;
using Revenant.Scenario;

namespace Revenant.Finance;

/// <summary>
/// Immutable screening rule set. A payment is held when the hard sanctions flag is set, or
/// <c>velocity15m &gt;= VelocityHoldThreshold AND amountMinor &gt;= VelocityAmountFloorMinor</c>, or, when
/// <see cref="NewBeneficiaryAmountFloorMinor"/> is set, <c>newBeneficiary AND amountMinor &gt;= floor</c>.
/// All amounts are integer minor units of <see cref="Currency"/>. Changing any field requires a new policy id.
/// </summary>
public sealed record PolicyConfig(
    string PolicyId,
    string FeatureSchemaVersion,
    string VelocityWindowSemantics,
    long VelocityWindowMs,
    string Currency,
    int VelocityHoldThreshold,
    long VelocityAmountFloorMinor,
    long? NewBeneficiaryAmountFloorMinor,
    bool HoldOnSanctionsHit)
{
    /// <summary>Canonical bytes of every rule field, prefixed by a config format version.</summary>
    public byte[] EncodeCanonical()
    {
        var writer = new CanonicalWriter()
            .WriteInt32(1)
            .WriteString(PolicyId)
            .WriteString(FeatureSchemaVersion)
            .WriteString(VelocityWindowSemantics)
            .WriteInt64(VelocityWindowMs)
            .WriteString(Currency)
            .WriteInt32(VelocityHoldThreshold)
            .WriteInt64(VelocityAmountFloorMinor)
            .WriteBoolean(NewBeneficiaryAmountFloorMinor.HasValue)
            .WriteInt64(NewBeneficiaryAmountFloorMinor ?? 0)
            .WriteBoolean(HoldOnSanctionsHit);
        return writer.ToArray();
    }

    public string ContentHash => ScenarioManifestCodec.Sha256Hex(EncodeCanonical());

    /// <summary>
    /// Screening decision from policy-visible features only. The synthetic truth label is deliberately not a
    /// parameter, so no policy can read it.
    /// </summary>
    public PaymentReason Screen(ScreeningFeatures features)
    {
        if (HoldOnSanctionsHit && features.SanctionsHit)
        {
            return PaymentReason.SanctionsHit;
        }
        if (features.Velocity15m >= VelocityHoldThreshold && features.AmountMinor >= VelocityAmountFloorMinor)
        {
            return PaymentReason.VelocityRule;
        }
        if (NewBeneficiaryAmountFloorMinor is { } floor && features.NewBeneficiary && features.AmountMinor >= floor)
        {
            return PaymentReason.NewBeneficiaryAmountRule;
        }
        return PaymentReason.None;
    }
}

/// <summary>Policy-visible inputs for one screening: derived velocity plus exogenous features.</summary>
public readonly record struct ScreeningFeatures(int Velocity15m, long AmountMinor, bool NewBeneficiary, bool SanctionsHit);

/// <summary>
/// Pinned policy identity, following the Revenant domain contract: id, config hash, model artifact hash and
/// feature schema hash. No learned model exists in this slice, so the artifact hash is <see cref="PolicyCatalog.NoModelArtifact"/>.
/// </summary>
public sealed record PolicyRef(string PolicyId, string ConfigHash, string ModelArtifactHash, string FeatureSchemaHash);

/// <summary>
/// Frozen policy-v7 and policy-v8 configurations. Resolution is exact on id and every hash; there is no
/// "latest" or default policy.
/// </summary>
public static class PolicyCatalog
{
    public const string V7Id = "policy-v7";
    public const string V8Id = "policy-v8";
    public const string NoModelArtifact = "none";

    /// <summary>
    /// Payer-scoped window: intents from the same source account already applied in logical sequence order,
    /// plus the current one, whose event time is within <c>[now - window, now]</c> inclusive.
    /// </summary>
    public const string PayerClosedWindowSemantics = "payer-source-account-closed-window/1";

    public const long FifteenMinutesMs = 15 * 60 * 1000L;

    public static PolicyConfig V7 { get; } = new(
        V7Id,
        ScenarioSchemas.FeatureSchemaVersion,
        PayerClosedWindowSemantics,
        FifteenMinutesMs,
        "USD",
        VelocityHoldThreshold: 8,
        VelocityAmountFloorMinor: 100_000,
        NewBeneficiaryAmountFloorMinor: null,
        HoldOnSanctionsHit: true);

    public static PolicyConfig V8 { get; } = new(
        V8Id,
        ScenarioSchemas.FeatureSchemaVersion,
        PayerClosedWindowSemantics,
        FifteenMinutesMs,
        "USD",
        VelocityHoldThreshold: 5,
        VelocityAmountFloorMinor: 0,
        NewBeneficiaryAmountFloorMinor: 150_000,
        HoldOnSanctionsHit: true);

    /// <summary>Hash of the feature schema version, its ordered feature names and the derived velocity window semantics.</summary>
    public static string FeatureSchemaHash(PolicyConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var writer = new CanonicalWriter()
            .WriteString(config.FeatureSchemaVersion)
            .WriteCount(FeatureNames.Length);
        foreach (var name in FeatureNames)
        {
            writer.WriteString(name);
        }
        writer.WriteString(config.VelocityWindowSemantics).WriteInt64(config.VelocityWindowMs);
        return ScenarioManifestCodec.Sha256Hex(writer.ToArray());
    }

    /// <summary>Exogenous feature names of <see cref="ScenarioSchemas.FeatureSchemaVersion"/> plus the derived velocity.</summary>
    public static readonly ImmutableArray<string> FeatureNames = ["newBeneficiary", "sanctionsHit", "velocity15m", "amountMinor"];

    public static PolicyRef RefOf(PolicyConfig config) =>
        new(config.PolicyId, config.ContentHash, NoModelArtifact, FeatureSchemaHash(config));

    public static Result<PolicyConfig, FinanceError> Resolve(PolicyRef pinned)
    {
        ArgumentNullException.ThrowIfNull(pinned);
        PolicyConfig? config = pinned.PolicyId switch
        {
            V7Id => V7,
            V8Id => V8,
            _ => null,
        };
        if (config is null)
        {
            return FinanceError.Fail<PolicyConfig>(FinanceErrorCode.VersionMismatch, $"Unknown policy '{pinned.PolicyId}'.");
        }
        var actual = RefOf(config);
        if (actual != pinned)
        {
            return FinanceError.Fail<PolicyConfig>(
                FinanceErrorCode.VersionMismatch,
                $"Pinned {pinned} does not match this build's {actual}.");
        }
        return Result<PolicyConfig, FinanceError>.Success(config);
    }
}
