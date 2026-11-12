using System.Collections.Immutable;
using System.Security.Cryptography;
using Marrow.Kernel.Canonical;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Intelligence;
using Revenant.Scenario;

namespace Revenant.Timeline;

internal enum V4ScreeningResult : byte
{
    BaseRuleHold = 1, ModelHoldAdmitted, ModelAboveThresholdCapacitySuppressed, BaseClear, BaseRuleRejection, WarmupRulesOnly,
}

internal readonly record struct V4ReviewCounts(long Eligible, long Mandatory, long Learned)
{
    internal const string Policy = "v4-model-review-capacity/1";
    internal const long WarmupMs = 86_400_000;

    internal long Slots => Eligible / 20;
    internal long Available => Math.Max(0, Slots - Mandatory - Learned);
    internal bool Valid => Eligible >= 0 && Mandatory >= 0 && Learned >= 0
        && Mandatory <= Eligible && Learned <= Eligible - Mandatory && Learned <= Slots;

    internal static bool InWarmup(ObservedFinanceContext context, long at) =>
        (Int128)at - context.OriginMs < WarmupMs;
}

/// <summary>The Screening payload and its own evidence reference are the same immutable fact.</summary>
internal sealed record V4ModelScreeningEvidence(
    ScenarioRunId RunId, string IntentId, long Sequence, long At, long PriorSequence, string PriorDigest, long LastSeal,
    string FeatureSchema, ImmutableArray<byte> Vector, string? VectorDigest, ObservedRiskPin Model, string FeatureSchemaHash,
    string Calibration, int? RawBits, int? Ppm, bool AboveThreshold, PolicyRef BasePolicy, PaymentReason BaseReason,
    ScreeningDecisionKind BaseDecision, V4ReviewCounts Capacity, long Allowed, long Available, bool Admitted,
    bool Suppressed, bool MandatoryOverload, V4ScreeningResult Decision, PaymentStatus Status, bool OpensCase)
{
    internal const string Schema = "v4-model-screening-evidence/1";
    private const string CalibrationIdentity = RiskArtifactManifestV2.Calibration;

    internal static bool Supports(ObservedFinanceContext context) =>
        context.RiskFeatureSchema == BehavioralRiskScorerV2.FeatureSchema && context.RiskModel == BehavioralRiskScorerV2.PublishedPin
        && context.ScenarioVersion == BehavioralEpisodeScenario.Version
        && (context.BasePolicy == PolicyCatalog.RefOf(PolicyCatalog.V7) || context.BasePolicy == PolicyCatalog.RefOf(PolicyCatalog.V8));

    internal static V4ModelScreeningEvidence Create(ObservedFinanceState prior, ObservedFinanceContext context, string intent,
        long at, ImmutableArray<byte> vector, int? rawBits, int? ppm)
    {
        Require(context.RiskMode == ObservedRiskMode.ModelRequired && Supports(context), "Unsupported V4 model pins.");
        Require(prior.Ledger.Payments.TryGetValue(intent, out var p) && p.Status == PaymentStatus.Initiated,
            "Model Screening requires an initiated payment.");
        var payment = p!;
        var warm = V4ReviewCounts.InWarmup(context, at);
        var model = context.RiskModel!;
        string? digest = null;
        if (warm)
        {
            Require(vector.IsEmpty && rawBits is null && ppm is null, "Warmup may not carry an inference.");
        }
        else
        {
            Require(rawBits is { } bits && float.IsFinite(BitConverter.Int32BitsToSingle(bits)) && ppm is >= 0 and <= 1_000_000,
                "Expected a finite raw score and integer PPM.");
            digest = RiskFeatureVectorV2.Decode(vector.AsSpan()).Digest;
        }

        var reason = FinanceLedgerReducer.ScreeningReason(payment.Intent, payment.Velocity15m, context.LegacyContext().Value.Policy);
        var mandatory = reason != PaymentReason.None;
        var funds = prior.Ledger.Accounts[payment.Intent.SourceAccountId].AvailableMinor >= payment.Intent.Amount.MinorUnits;
        var counts = warm ? prior.ReviewCounts : prior.ReviewCounts with { Eligible = checked(prior.ReviewCounts.Eligible + 1) };
        Require(counts.Valid && (!warm || counts == default), "Invalid V4 review capacity prefix.");
        var above = ppm >= model.ThresholdPpm;
        var admitted = !mandatory && above && counts.Available > 0 && funds;
        var suppressed = !mandatory && above && counts.Available == 0;
        var decision = warm ? V4ScreeningResult.WarmupRulesOnly
            : mandatory ? (funds ? V4ScreeningResult.BaseRuleHold : V4ScreeningResult.BaseRuleRejection)
            : admitted ? V4ScreeningResult.ModelHoldAdmitted
            : suppressed ? V4ScreeningResult.ModelAboveThresholdCapacitySuppressed : V4ScreeningResult.BaseClear;
        var status = mandatory ? (funds ? PaymentStatus.Held : PaymentStatus.Rejected)
            : admitted ? PaymentStatus.Held : PaymentStatus.Cleared;
        return new(prior.RunId, intent, checked(prior.LastFact + 1), at, prior.LastFact, prior.PrefixDigest,
            prior.LastSealedBoundaryMs, context.RiskFeatureSchema, vector, digest, model, RiskFeatureVectorV2.SchemaHash,
            CalibrationIdentity, rawBits, ppm, above, context.BasePolicy, reason,
            mandatory ? ScreeningDecisionKind.Held : ScreeningDecisionKind.Clear, counts, counts.Slots, counts.Available,
            admitted, suppressed, !warm && counts.Mandatory + (decision == V4ScreeningResult.BaseRuleHold ? 1 : 0) > counts.Slots,
            decision, status, status == PaymentStatus.Held);
    }

    internal PaymentReason EffectiveReason => Admitted ? PaymentReason.MlRiskHold : BaseReason;

    internal V4ReviewCounts NextCounts => Capacity with
    {
        Learned = checked(Capacity.Learned + (Admitted ? 1 : 0)),
        Mandatory = checked(Capacity.Mandatory + (Decision == V4ScreeningResult.BaseRuleHold ? 1 : 0)),
    };

    internal ObservedFinanceInput ToInput()
    {
        var bytes = Encode();
        return new(RunId, Sequence, At, ObservedFactKind.Screening, Schema, [.. bytes], Schema, Hash(bytes));
    }

    private byte[] Encode()
    {
        var w = new CanonicalWriter().WriteInt32(1).WriteString(Schema).WriteGuid(RunId.Value).WriteString(IntentId)
            .WriteInt64(Sequence).WriteInt64(At).WriteInt64(PriorSequence).WriteBytes(Convert.FromHexString(PriorDigest))
            .WriteInt64(LastSeal).WriteString(FeatureSchema).WriteBytes(Vector.AsSpan()).WriteBoolean(RawBits.HasValue);
        if (RawBits is { } raw)
            w.WriteBytes(Convert.FromHexString(VectorDigest!)).WriteInt32(raw).WriteInt32(Ppm!.Value);
        return w.WriteString(Model.ModelId).WriteBytes(Convert.FromHexString(Model.ArtifactSha256))
            .WriteInt32(Model.ThresholdPpm).WriteBytes(Convert.FromHexString(FeatureSchemaHash)).WriteString(Calibration)
            .WriteBoolean(AboveThreshold).WriteString(BasePolicy.PolicyId).WriteString(BasePolicy.ConfigHash)
            .WriteString(BasePolicy.ModelArtifactHash).WriteString(BasePolicy.FeatureSchemaHash).WriteByte((byte)BaseReason)
            .WriteByte((byte)BaseDecision).WriteString(V4ReviewCounts.Policy)
            .WriteInt64(Capacity.Eligible).WriteInt64(Capacity.Mandatory).WriteInt64(Capacity.Learned)
            .WriteInt64(Allowed).WriteInt64(Available).WriteBoolean(Admitted).WriteBoolean(Suppressed).WriteBoolean(MandatoryOverload)
            .WriteByte((byte)Decision).WriteByte((byte)Status).WriteBoolean(OpensCase).ToArray();
    }

    internal static V4ModelScreeningEvidence Decode(ObservedFinanceInput input)
    {
        var bytes = input.Payload.AsSpan();
        Require(input.Kind == ObservedFactKind.Screening && input.PayloadSchema == Schema && input.EvidenceSchema == Schema
            && input.EvidenceSha256 == Hash(bytes), "Model Screening evidence reference does not match its payload.");
        var r = new CanonicalReader(bytes);
        ObservedFinanceEncoding.ReadFormat(ref r);
        Require(r.ReadString() == Schema, "Unknown model Screening evidence version.");
        ScenarioRunId run = new(r.ReadGuid());
        var intent = r.ReadString();
        var seq = r.ReadInt64();
        var at = r.ReadInt64();
        var prior = r.ReadInt64();
        var digest = ObservedFinanceEncoding.ReadHash(ref r);
        var seal = r.ReadInt64();
        var features = r.ReadString();
        ImmutableArray<byte> vector = [.. r.ReadBytes()];
        string? vectorDigest = null;
        int? raw = null, ppm = null;
        if (r.ReadBoolean())
        {
            vectorDigest = ObservedFinanceEncoding.ReadHash(ref r);
            raw = r.ReadInt32();
            ppm = r.ReadInt32();
        }
        ObservedRiskPin pin = new(r.ReadString(), ObservedFinanceEncoding.ReadHash(ref r), r.ReadInt32());
        var schemaHash = ObservedFinanceEncoding.ReadHash(ref r);
        var calibration = r.ReadString();
        var above = r.ReadBoolean();
        PolicyRef policy = new(r.ReadString(), r.ReadString(), r.ReadString(), r.ReadString());
        var reason = (PaymentReason)r.ReadByte();
        var baseDecision = (ScreeningDecisionKind)r.ReadByte();
        Require(r.ReadString() == V4ReviewCounts.Policy, "Unknown model review capacity policy.");
        var evidence = new V4ModelScreeningEvidence(run, intent, seq, at, prior, digest, seal, features, vector, vectorDigest,
            pin, schemaHash, calibration, raw, ppm, above, policy, reason, baseDecision, new(r.ReadInt64(), r.ReadInt64(), r.ReadInt64()),
            r.ReadInt64(), r.ReadInt64(), r.ReadBoolean(), r.ReadBoolean(), r.ReadBoolean(), (V4ScreeningResult)r.ReadByte(),
            (PaymentStatus)r.ReadByte(), r.ReadBoolean());
        r.EnsureEnd();
        Require(run == input.RunId && seq == input.Sequence && at == input.EffectiveTimeMs && bytes.SequenceEqual(evidence.Encode()),
            "Model Screening evidence does not match its envelope.");
        return evidence;
    }

    internal void Verify(ObservedFinanceState prior, ObservedFinanceContext context, ObservedFinanceInput input)
    {
        var expected = Create(prior, context, IntentId, input.EffectiveTimeMs, Vector, RawBits, Ppm);
        Require(input.Payload.AsSpan().SequenceEqual(expected.Encode()), "Model Screening evidence disagrees with the pinned prefix or policy.");
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new CanonicalFormatException(message);
    }
}
