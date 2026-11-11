using System.Security.Cryptography;
using Marrow.Kernel.Canonical;
using Marrow.Kernel.Results;
using Revenant.Contracts;

namespace Revenant.Intelligence;

public enum PpmError
{
    NotFinite = 1,
    OutOfUnitRange = 2,
}

/// <summary>
/// The one place a model <c>Probability</c> becomes the integer score the policy, audit and Timeline bytes use.
/// <c>float</c> is exact in <c>double</c>, and a 24-bit float mantissa times 10^6 (20 bits) fits the 53-bit double mantissa,
/// so <c>p * 1e6</c> is computed exactly; it is then rounded half away from zero. No decimal or locale step is involved.
/// </summary>
public static class RiskScoreNormalization
{
    public const string NormalizationId = "probability-ppm/1";
    public const int PpmScale = 1_000_000;

    public static Result<int, PpmError> FromProbability(float probability)
    {
        if (!float.IsFinite(probability))
        {
            return Result<int, PpmError>.Failure(PpmError.NotFinite);
        }
        if (probability < 0f || probability > 1f)
        {
            return Result<int, PpmError>.Failure(PpmError.OutOfUnitRange);
        }
        var scaled = (double)probability * PpmScale;
        return Result<int, PpmError>.Success((int)Math.Round(scaled, MidpointRounding.AwayFromZero));
    }

    /// <summary>The fixed tie rule: a learned hold when <c>scorePpm &gt;= thresholdPpm</c>.</summary>
    public static bool AtOrAboveThreshold(int scorePpm, int thresholdPpm) => scorePpm >= thresholdPpm;
}

/// <summary>Outcome class of a screening decision, used for both the rules-only base decision and the final decision.</summary>
public enum ScreeningDecisionKind : byte
{
    Clear = 1,
    Held = 2,
    Rejected = 3,
}

public enum EvidenceError
{
    Malformed = 1,
    FeatureHashMismatch = 2,
    DecisionInconsistent = 3,
    IdentityMismatch = 4,
}

/// <summary>
/// <c>InferenceEvidenceV1</c> (specified in Spec 08 task 8.1, first persisted and consumed in 8.3): the minimum canonical record that
/// binds one learned screening score to the exact pre-screening facts, model, policy and decision. All numbers are integers.
/// Decision precedence is part of its validity: base Rejected and base Held stay as they are; a base Clear becomes Held iff
/// <c>ScorePpm &gt;= ThresholdPpm</c>. The score never releases or approves anything.
/// <para>
/// Combined screening input boundary (for 8.3): one ordered input = the immutable <see cref="PaymentIntent"/> bytes plus these evidence
/// bytes. The reducer applies the screening transition only when both are present and <see cref="CheckBinds"/> holds; the evidence is
/// produced outside the reducer, before the combined fact is committed. No inference runs inside a reducer.
/// </para>
/// </summary>
public sealed record InferenceEvidenceV1
{
    public const int FormatVersion = 1;
    public const string SchemaId = "revenant-inference-evidence/1";
    public const int MaxThresholdPpm = RiskScoreNormalization.PpmScale;

    public InferenceEvidenceV1(
        ScenarioRunId runId,
        string intentId,
        long asOfFactSeq,
        long screeningFactSeq,
        RiskFeatureRaw features,
        string modelId,
        string modelArtifactSha256,
        string modelManifestSha256,
        string mlNetPackageVersion,
        string policyId,
        int thresholdPpm,
        int scorePpm,
        ScreeningDecisionKind baseDecision)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentException.ThrowIfNullOrWhiteSpace(intentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(mlNetPackageVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyId);
        if (runId.IsEmpty)
        {
            throw new ArgumentException("ScenarioRunId must not be empty.", nameof(runId));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(asOfFactSeq);
        if (screeningFactSeq != checked(asOfFactSeq + 1))
        {
            throw new ArgumentException("The screening fact must be the one immediately after the as-of boundary.", nameof(screeningFactSeq));
        }
        RequireSha256(modelArtifactSha256, nameof(modelArtifactSha256));
        RequireSha256(modelManifestSha256, nameof(modelManifestSha256));
        ArgumentOutOfRangeException.ThrowIfLessThan(thresholdPpm, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(thresholdPpm, MaxThresholdPpm);
        ArgumentOutOfRangeException.ThrowIfNegative(scorePpm);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(scorePpm, RiskScoreNormalization.PpmScale);
        if (!Enum.IsDefined(baseDecision))
        {
            throw new ArgumentException("Unknown base decision.", nameof(baseDecision));
        }

        RunId = runId;
        IntentId = intentId;
        AsOfFactSeq = asOfFactSeq;
        ScreeningFactSeq = screeningFactSeq;
        Features = features;
        ModelId = modelId;
        ModelArtifactSha256 = modelArtifactSha256;
        ModelManifestSha256 = modelManifestSha256;
        MlNetPackageVersion = mlNetPackageVersion;
        PolicyId = policyId;
        ThresholdPpm = thresholdPpm;
        ScorePpm = scorePpm;
        BaseDecision = baseDecision;
        LearnedHold = RiskScoreNormalization.AtOrAboveThreshold(scorePpm, thresholdPpm);
        FinalDecision = Decide(baseDecision, LearnedHold);
    }

    public ScenarioRunId RunId { get; }

    public string IntentId { get; }

    /// <summary>Highest run-local fact sequence visible to the feature snapshot.</summary>
    public long AsOfFactSeq { get; }

    /// <summary>Run-local sequence of the screening fact this evidence belongs to; always <c>AsOfFactSeq + 1</c>.</summary>
    public long ScreeningFactSeq { get; }

    public RiskFeatureRaw Features { get; }

    public string FeatureSchemaId => RiskFeaturesV1.SchemaId;

    public string FeatureHash => Features.FeatureHash;

    public string ModelId { get; }

    public string ModelArtifactSha256 { get; }

    public string ModelManifestSha256 { get; }

    public string MlNetPackageVersion { get; }

    public string NormalizationId => RiskScoreNormalization.NormalizationId;

    public string PolicyId { get; }

    public int ThresholdPpm { get; }

    public int ScorePpm { get; }

    public ScreeningDecisionKind BaseDecision { get; }

    /// <summary>Derived: <c>ScorePpm &gt;= ThresholdPpm</c>.</summary>
    public bool LearnedHold { get; }

    /// <summary>Derived from <see cref="BaseDecision"/> and <see cref="LearnedHold"/>.</summary>
    public ScreeningDecisionKind FinalDecision { get; }

    public static ScreeningDecisionKind Decide(ScreeningDecisionKind baseDecision, bool learnedHold) =>
        baseDecision == ScreeningDecisionKind.Clear && learnedHold ? ScreeningDecisionKind.Held : baseDecision;

    /// <summary>The evidence must describe exactly this immutable intent (run, id, currency and amount).</summary>
    public Result<bool, EvidenceError> CheckBinds(PaymentIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var ok = intent.RunId == RunId
            && string.Equals(intent.IntentId, IntentId, StringComparison.Ordinal)
            && string.Equals(intent.Amount.Currency, Features.Currency, StringComparison.Ordinal)
            && intent.Amount.MinorUnits == Features.AmountMinor;
        return ok
            ? Result<bool, EvidenceError>.Success(true)
            : Result<bool, EvidenceError>.Failure(EvidenceError.IdentityMismatch);
    }

    /// <summary>
    /// Canonical bytes, format 1, in this field order: format, run, intent, as-of seq, screening seq, feature schema id, currency,
    /// amount minor, source posted before minor, feature hash, model id, artifact sha256, manifest sha256, ML.NET package version,
    /// normalization id, policy id, threshold ppm, score ppm, base decision, learned hold, final decision.
    /// </summary>
    public byte[] ToCanonicalBytes() => new CanonicalWriter()
        .WriteInt32(FormatVersion)
        .WriteGuid(RunId.Value)
        .WriteString(IntentId)
        .WriteInt64(AsOfFactSeq)
        .WriteInt64(ScreeningFactSeq)
        .WriteString(FeatureSchemaId)
        .WriteString(Features.Currency)
        .WriteInt64(Features.AmountMinor)
        .WriteInt64(Features.SourcePostedBeforeMinor)
        .WriteString(FeatureHash)
        .WriteString(ModelId)
        .WriteString(ModelArtifactSha256)
        .WriteString(ModelManifestSha256)
        .WriteString(MlNetPackageVersion)
        .WriteString(NormalizationId)
        .WriteString(PolicyId)
        .WriteInt32(ThresholdPpm)
        .WriteInt32(ScorePpm)
        .WriteByte((byte)BaseDecision)
        .WriteBoolean(LearnedHold)
        .WriteByte((byte)FinalDecision)
        .ToArray();

    public string EvidenceHash => Convert.ToHexStringLower(SHA256.HashData(ToCanonicalBytes()));

    /// <summary>
    /// Strict decode: rejects an unknown format, trailing or missing bytes, a stored feature hash that does not match the stored
    /// raw integers, and stored derived fields (hold flag, final decision) that disagree with the decision rule.
    /// </summary>
    public static Result<InferenceEvidenceV1, EvidenceError> Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var reader = new CanonicalReader(bytes);
            if (reader.ReadInt32() != FormatVersion)
            {
                return Result<InferenceEvidenceV1, EvidenceError>.Failure(EvidenceError.Malformed);
            }
            var run = new ScenarioRunId(reader.ReadGuid());
            var intentId = reader.ReadString();
            var asOf = reader.ReadInt64();
            var screeningSeq = reader.ReadInt64();
            var schema = reader.ReadString();
            var currency = reader.ReadString();
            var amount = reader.ReadInt64();
            var posted = reader.ReadInt64();
            var storedHash = reader.ReadString();
            var modelId = reader.ReadString();
            var artifact = reader.ReadString();
            var manifest = reader.ReadString();
            var package = reader.ReadString();
            var normalization = reader.ReadString();
            var policy = reader.ReadString();
            var threshold = reader.ReadInt32();
            var score = reader.ReadInt32();
            var baseDecision = (ScreeningDecisionKind)reader.ReadByte();
            var learnedHold = reader.ReadBoolean();
            var finalDecision = (ScreeningDecisionKind)reader.ReadByte();
            reader.EnsureEnd();

            if (schema != RiskFeaturesV1.SchemaId || normalization != RiskScoreNormalization.NormalizationId)
            {
                return Result<InferenceEvidenceV1, EvidenceError>.Failure(EvidenceError.Malformed);
            }
            var built = RiskFeaturesV1.Build(currency, amount, posted);
            if (built.IsFailure)
            {
                return Result<InferenceEvidenceV1, EvidenceError>.Failure(EvidenceError.Malformed);
            }
            if (!string.Equals(built.Value.FeatureHash, storedHash, StringComparison.Ordinal))
            {
                return Result<InferenceEvidenceV1, EvidenceError>.Failure(EvidenceError.FeatureHashMismatch);
            }
            var evidence = new InferenceEvidenceV1(
                run, intentId, asOf, screeningSeq, built.Value.Raw, modelId, artifact, manifest, package, policy, threshold, score, baseDecision);
            return evidence.LearnedHold == learnedHold && evidence.FinalDecision == finalDecision
                ? Result<InferenceEvidenceV1, EvidenceError>.Success(evidence)
                : Result<InferenceEvidenceV1, EvidenceError>.Failure(EvidenceError.DecisionInconsistent);
        }
        catch (Exception ex) when (ex is CanonicalFormatException or ArgumentException)
        {
            return Result<InferenceEvidenceV1, EvidenceError>.Failure(EvidenceError.Malformed);
        }
    }

    private static void RequireSha256(string value, string name)
    {
        if (value is null || value.Length != 64 || !value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("Expected a lower-case hex SHA-256.", name);
        }
    }
}
