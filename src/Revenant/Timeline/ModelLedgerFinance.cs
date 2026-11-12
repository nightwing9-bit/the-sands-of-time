using System.Collections.Immutable;
using System.Security.Cryptography;
using Marrow.Kernel.Canonical;
using Marrow.Kernel.Results;
using Marrow.Timeline;
using Revenant.Cases;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Intelligence;
using Revenant.Scenario;

namespace Revenant.Timeline;

/// <summary>
/// Identity of the additive model-aware finance history version (Spec 08 task 8.3, ADR-P1-20). A third version family beside V1
/// (<see cref="FinanceTimelineVersions"/>) and V2 (<see cref="FinanceLedgerVersion"/>), for newly opened model-required durable runs
/// only. V1 and V2 keys, reducers, codecs, bytes and hashes are untouched and stay registered. A run's family is recorded in
/// <c>revenant.scenario_run.finance_version</c> at opening and never changes, so a V1/V2 history cannot become V3.
/// </summary>
public static class ModelLedgerVersion
{
    public const string DomainId = FinanceLedgerVersion.DomainId;
    public const string ReducerVersion = "revenant-finance-reducer/3";
    public const string InputSchemaVersion = "revenant-finance-fact/3";
    public const string StateSchemaVersion = "revenant-finance-state/3";
    public const string ContextSchemaVersion = "revenant-finance-context/3";

    /// <summary>
    /// Payload schema of a V3 <see cref="FinanceFactKind.Screening"/> fact: the payload IS the canonical
    /// <see cref="InferenceEvidenceV1"/> bytes (which name the run and intent), and the fact's evidence reference is
    /// <c>(this schema, SHA-256 of the payload)</c>. The screening transition and its evidence are therefore one ordered input.
    /// </summary>
    public const string ScreeningSchema = InferenceEvidenceV1.SchemaId;
}

/// <summary>
/// Pinned context of a V3 history: the V2 base context (scenario manifest, policy v7, review policy) whose rules decide every
/// non-learned outcome, and the model-aware <see cref="RiskPolicy"/> (model identity and threshold). Stored with the history; a cold
/// replay never consults a "current" model or default.
/// </summary>
public sealed record ModelLedgerContext
{
    private ModelLedgerContext(LedgerFinanceContext baseContext, RiskPolicy risk) => (Base, Risk) = (baseContext, risk);

    /// <summary>The V2 base context (reducer <c>/2</c>, policy v7) that the V3 reducer delegates rules-only facts to.</summary>
    public LedgerFinanceContext Base { get; }

    public RiskPolicy Risk { get; }

    public string ReducerVersion => ModelLedgerVersion.ReducerVersion;

    public static Result<ModelLedgerContext, LedgerFinanceError> Pin(ScenarioManifest manifest, RiskPolicy risk)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(risk);
        var basePinned = LedgerFinanceContext.Pin(manifest, risk.BasePolicy);
        return basePinned.TryGetValue(out var b)
            ? FromPins(b.ScenarioVersion, b.ManifestContentHash, ModelLedgerVersion.ReducerVersion, risk.BasePolicy, b.ReviewPolicyVersion, risk.PolicyId, risk.Model)
            : Result<ModelLedgerContext, LedgerFinanceError>.Failure(basePinned.Error);
    }

    public static Result<ModelLedgerContext, LedgerFinanceError> FromPins(
        string scenarioVersion, string manifestHash, string reducerVersion, PolicyRef basePolicy, string reviewPolicyVersion,
        string riskPolicyId, RiskModelIdentity model)
    {
        ArgumentNullException.ThrowIfNull(basePolicy);
        ArgumentNullException.ThrowIfNull(model);
        if (!string.Equals(reducerVersion, ModelLedgerVersion.ReducerVersion, StringComparison.Ordinal))
        {
            return Fail($"Reducer version '{reducerVersion}' is not '{ModelLedgerVersion.ReducerVersion}'.");
        }
        var risk = RiskPolicyCatalog.Resolve(riskPolicyId, model);
        if (!risk.TryGetValue(out var policy))
        {
            return Fail(risk.Error);
        }
        if (policy.BasePolicy != basePolicy)
        {
            return Fail($"Base policy {basePolicy} is not {riskPolicyId}'s {policy.BasePolicy}.");
        }
        var basePinned = LedgerFinanceContext.FromPins(scenarioVersion, manifestHash, FinanceLedgerVersion.ReducerVersion, basePolicy, reviewPolicyVersion);
        return basePinned.TryGetValue(out var b)
            ? Result<ModelLedgerContext, LedgerFinanceError>.Success(new ModelLedgerContext(b, policy))
            : Result<ModelLedgerContext, LedgerFinanceError>.Failure(basePinned.Error);
    }

    /// <summary>
    /// Format 1: scenario version, manifest hash, reducer version (<c>/3</c>), base policy id/config/model/feature hashes, review policy
    /// version, risk policy id, model id, artifact SHA-256, manifest SHA-256, ML.NET version, feature schema id, normalization id,
    /// threshold PPM.
    /// </summary>
    public byte[] EncodeCanonical() => new CanonicalWriter()
        .WriteInt32(1)
        .WriteString(Base.ScenarioVersion)
        .WriteString(Base.ManifestContentHash)
        .WriteString(ReducerVersion)
        .WriteString(Base.PolicyRef.PolicyId)
        .WriteString(Base.PolicyRef.ConfigHash)
        .WriteString(Base.PolicyRef.ModelArtifactHash)
        .WriteString(Base.PolicyRef.FeatureSchemaHash)
        .WriteString(Base.ReviewPolicyVersion)
        .WriteString(Risk.PolicyId)
        .WriteString(Risk.Model.ModelId)
        .WriteString(Risk.Model.ArtifactSha256)
        .WriteString(Risk.Model.ManifestSha256)
        .WriteString(Risk.Model.MlNetPackageVersion)
        .WriteString(Risk.Model.FeatureSchemaId)
        .WriteString(RiskScoreNormalization.NormalizationId)
        .WriteInt32(Risk.Model.ThresholdPpm)
        .ToArray();

    public string ContentHash => ScenarioManifestCodec.Sha256Hex(EncodeCanonical());

    private static Result<ModelLedgerContext, LedgerFinanceError> Fail(string message) =>
        Result<ModelLedgerContext, LedgerFinanceError>.Failure(new LedgerFinanceError(LedgerFinanceErrorCode.VersionMismatch, message));
}

/// <summary>The integer, replayed summary of one payment's recorded learned screening (the full evidence stays in the fact bytes).</summary>
public sealed record LearnedScreening(
    long FactSeq,
    string EvidenceSha256,
    string FeatureHash,
    long SourcePostedBeforeMinor,
    int ScorePpm,
    int ThresholdPpm,
    ScreeningDecisionKind BaseDecision,
    bool LearnedHold,
    ScreeningDecisionKind FinalDecision);

/// <summary>Complete V3 state: the V2-shaped ledger fold plus every recorded learned screening, keyed by intent id.</summary>
public sealed record ModelLedgerState(LedgerFinanceState Ledger, ImmutableSortedDictionary<string, LearnedScreening> Screenings)
{
    public long LastFact => Ledger.LastFact;

    public ScenarioRunId RunId => Ledger.RunId;
}

/// <summary>
/// Pure V3 finance reducer (<see cref="ModelLedgerVersion.ReducerVersion"/>). Every fact except Screening is applied by the frozen V2
/// <see cref="FinanceLedgerReducer"/> under the pinned base context, so intake, review, settlement and return keep exactly their
/// Spec 07 semantics. A Screening fact must carry its <see cref="InferenceEvidenceV1"/> (a V2 evidence-free screening fact is
/// refused); the reducer decodes it, checks that it describes this intent, this fact position, the source's committed posted balance
/// in this state, the pinned model/policy/threshold and the v7 base decision recomputed here, and then applies the recorded final
/// decision: base hold keeps its v7 reason, a base-clear payment whose recorded score reached the threshold is held with
/// <see cref="PaymentReason.MlRiskHold"/>; any hold reserves and opens a case when funds cover it, else Held → Rejected(insufficient).
/// It runs no model, reads no clock, file or database and computes no floating-point score; the recorded integer PPM is the input.
/// </summary>
public static class ModelLedgerReducer
{
    public static Result<ModelLedgerState, LedgerFinanceError> Initial(ScenarioRun run, ModelLedgerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var initial = FinanceLedgerReducer.Initial(run, context.Base);
        return initial.TryGetValue(out var ledger)
            ? Ok(new ModelLedgerState(ledger, ImmutableSortedDictionary.Create<string, LearnedScreening>(StringComparer.Ordinal)))
            : Result<ModelLedgerState, LedgerFinanceError>.Failure(initial.Error);
    }

    public static Result<ModelLedgerState, LedgerFinanceError> Apply(ModelLedgerState state, FinanceFact fact, ModelLedgerContext context)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(context);
        if (fact.Kind != FinanceFactKind.Screening)
        {
            if (fact.Evidence is not null)
            {
                return Fail(LedgerFinanceErrorCode.MalformedFact, $"Fact {fact.Sequence} ({fact.Kind}) carries inference evidence; only screening does.");
            }
            var next = FinanceLedgerReducer.Apply(state.Ledger, fact, context.Base);
            return next.TryGetValue(out var ledger) ? Ok(state with { Ledger = ledger }) : Result<ModelLedgerState, LedgerFinanceError>.Failure(next.Error);
        }
        var s = state.Ledger;
        if (!string.Equals(context.Base.ManifestContentHash, s.ManifestContentHash, StringComparison.Ordinal)
            || !string.Equals(context.Base.ScenarioVersion, s.ScenarioVersion, StringComparison.Ordinal))
        {
            return Fail(LedgerFinanceErrorCode.VersionMismatch, "Pinned context is not compatible with this state.");
        }
        if (fact.Sequence != s.LastFact + 1)
        {
            return Fail(LedgerFinanceErrorCode.OutOfOrderFact, $"Fact {fact.Sequence} does not follow fact {s.LastFact}.");
        }
        if (!string.Equals(fact.PayloadSchema, ModelLedgerVersion.ScreeningSchema, StringComparison.Ordinal))
        {
            return Fail(LedgerFinanceErrorCode.MalformedFact,
                $"Screening fact {fact.Sequence} has payload schema '{fact.PayloadSchema}', not '{ModelLedgerVersion.ScreeningSchema}': a model-required run cannot screen without evidence.");
        }
        var evidenceSha = SHA256.HashData(fact.Payload);
        if (fact.Evidence is not { } reference || !string.Equals(reference.Schema, InferenceEvidenceV1.SchemaId, StringComparison.Ordinal)
            || !reference.Sha256.AsSpan().SequenceEqual(evidenceSha))
        {
            return Fail(LedgerFinanceErrorCode.MalformedFact, $"Screening fact {fact.Sequence} has no matching inference evidence reference.");
        }
        var decoded = InferenceEvidenceV1.Decode(fact.Payload);
        if (!decoded.TryGetValue(out var evidence))
        {
            return Fail(decoded.Error == EvidenceError.Malformed ? LedgerFinanceErrorCode.MalformedFact : LedgerFinanceErrorCode.InconsistentFact,
                $"Screening fact {fact.Sequence}: evidence {decoded.Error}.");
        }
        if (!s.Payments.TryGetValue(evidence.IntentId, out var p))
        {
            return Fail(LedgerFinanceErrorCode.UnknownPayment, $"Screening names unknown intent {evidence.IntentId}.");
        }
        if (p.Status != PaymentStatus.Initiated)
        {
            return Fail(LedgerFinanceErrorCode.IllegalStep, $"Intent {evidence.IntentId} is {p.Status}, not Initiated, at screening.");
        }
        if (evidence.CheckBinds(p.Intent).IsFailure || evidence.ScreeningFactSeq != fact.Sequence)
        {
            return Fail(LedgerFinanceErrorCode.InconsistentFact, $"Evidence of fact {fact.Sequence} does not describe intent {p.Intent.IntentId} at this position.");
        }
        var model = context.Risk.Model;
        if (!string.Equals(evidence.PolicyId, context.Risk.PolicyId, StringComparison.Ordinal)
            || !string.Equals(evidence.ModelId, model.ModelId, StringComparison.Ordinal)
            || !string.Equals(evidence.ModelArtifactSha256, model.ArtifactSha256, StringComparison.Ordinal)
            || !string.Equals(evidence.ModelManifestSha256, model.ManifestSha256, StringComparison.Ordinal)
            || !string.Equals(evidence.MlNetPackageVersion, model.MlNetPackageVersion, StringComparison.Ordinal)
            || !string.Equals(evidence.FeatureSchemaId, model.FeatureSchemaId, StringComparison.Ordinal)
            || evidence.ThresholdPpm != model.ThresholdPpm)
        {
            return Fail(LedgerFinanceErrorCode.VersionMismatch, $"Evidence of fact {fact.Sequence} is not from the pinned model and policy {context.Risk.PolicyId}.");
        }
        var source = s.Accounts[p.Intent.SourceAccountId];
        if (evidence.Features.SourcePostedBeforeMinor != source.PostedMinor)
        {
            return Fail(LedgerFinanceErrorCode.InconsistentFact,
                $"Evidence of fact {fact.Sequence} used posted balance {evidence.Features.SourcePostedBeforeMinor}, but the source's committed posted balance here is {source.PostedMinor}.");
        }
        var baseReason = FinanceLedgerReducer.ScreeningReason(p.Intent, p.Velocity15m, context.Base.Policy);
        var expectedBase = BaseDecision(baseReason);
        if (evidence.BaseDecision != expectedBase)
        {
            return Fail(LedgerFinanceErrorCode.InconsistentFact, $"Evidence of fact {fact.Sequence} records base {evidence.BaseDecision}; policy v7 decides {expectedBase}.");
        }
        var reason = EffectiveReason(baseReason, evidence.FinalDecision);
        var screened = Screen(s, p, reason) with { LastFact = fact.Sequence };
        var summary = new LearnedScreening(fact.Sequence, Convert.ToHexStringLower(evidenceSha), evidence.FeatureHash, evidence.Features.SourcePostedBeforeMinor,
            evidence.ScorePpm, evidence.ThresholdPpm, evidence.BaseDecision, evidence.LearnedHold, evidence.FinalDecision);
        return Ok(new ModelLedgerState(screened, state.Screenings.Add(p.Intent.IntentId, summary)));
    }

    public static Result<ModelLedgerState, LedgerFinanceError> ApplyAll(ModelLedgerState state, IEnumerable<FinanceFact> facts, ModelLedgerContext context)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var current = state;
        foreach (var fact in facts)
        {
            var next = Apply(current, fact, context);
            if (!next.TryGetValue(out current!))
            {
                return next;
            }
        }
        return Ok(current);
    }

    /// <summary>The v7 policy decision class (funds are a ledger consequence, not a policy decision): Clear iff no hold reason.</summary>
    public static ScreeningDecisionKind BaseDecision(PaymentReason baseReason) =>
        baseReason == PaymentReason.None ? ScreeningDecisionKind.Clear : ScreeningDecisionKind.Held;

    /// <summary>The hold reason the final decision implies: the v7 reason has precedence; a learned hold of a base-clear payment is <see cref="PaymentReason.MlRiskHold"/>.</summary>
    public static PaymentReason EffectiveReason(PaymentReason baseReason, ScreeningDecisionKind finalDecision) =>
        baseReason != PaymentReason.None ? baseReason
        : finalDecision == ScreeningDecisionKind.Held ? PaymentReason.MlRiskHold
        : PaymentReason.None;

    // The V2 screening consequence, for an already decided reason (same trail, reservation, case and insufficient-funds rule).
    internal static LedgerFinanceState Screen(LedgerFinanceState state, LedgerPayment p, PaymentReason reason)
    {
        var source = state.Accounts[p.Intent.SourceAccountId];
        var amount = p.Intent.Amount.MinorUnits;
        if (reason == PaymentReason.None)
        {
            return Put(state, p with { Status = PaymentStatus.Cleared, Trail = [.. p.Trail, PaymentStatus.Screened, PaymentStatus.Cleared] });
        }
        if (source.AvailableMinor < amount)
        {
            return Put(state, p with
            {
                Status = PaymentStatus.Rejected,
                Reason = PaymentReason.InsufficientAvailable,
                Trail = [.. p.Trail, PaymentStatus.Screened, PaymentStatus.Held, PaymentStatus.Rejected],
            });
        }
        var held = Put(state, p with
        {
            Status = PaymentStatus.Held,
            Reason = reason,
            Trail = [.. p.Trail, PaymentStatus.Screened, PaymentStatus.Held],
            ReservedMinor = amount,
            Case = CaseStatus.Open,
            RequiredApprovals = ReviewPolicy.RequiredApprovals(p.Intent.Amount.Currency, amount),
        });
        return held with { Accounts = held.Accounts.SetItem(source.AccountId, source with { ReservedMinor = checked(source.ReservedMinor + amount) }) };
    }

    private static LedgerFinanceState Put(LedgerFinanceState state, LedgerPayment payment) =>
        state with { Payments = state.Payments.SetItem(payment.Intent.IntentId, payment) };

    private static Result<ModelLedgerState, LedgerFinanceError> Ok(ModelLedgerState state) =>
        Result<ModelLedgerState, LedgerFinanceError>.Success(state);

    private static Result<ModelLedgerState, LedgerFinanceError> Fail(LedgerFinanceErrorCode code, string message) =>
        Result<ModelLedgerState, LedgerFinanceError>.Failure(new LedgerFinanceError(code, message));
}

/// <summary>The V3 Timeline transition: position <c>k</c> is exactly fact <c>k</c> of the run (same input id rule as V2).</summary>
public sealed class ModelLedgerTransition : IDeterministicTransition<ModelLedgerState, FinanceFact, ModelLedgerContext>
{
    public static ModelLedgerTransition Instance { get; } = new();

    public ModelLedgerState Apply(
        ModelLedgerState previous, long position, IReadOnlyList<OrderedInput<FinanceFact>> inputs, ModelLedgerContext pinnedContext)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count != 1 || inputs[0].Payload.Sequence != position
            || !string.Equals(inputs[0].InputId, FinanceLedgerTransition.InputId(previous.RunId, position), StringComparison.Ordinal))
        {
            throw new FinanceLedgerTransitionException(new LedgerFinanceError(
                LedgerFinanceErrorCode.OutOfOrderFact, $"Position {position} must carry exactly fact {position} of run {previous.RunId}."));
        }
        var next = ModelLedgerReducer.Apply(previous, inputs[0].Payload, pinnedContext);
        return next.TryGetValue(out var state) ? state : throw new FinanceLedgerTransitionException(next.Error);
    }
}

/// <summary>Context codec of V3 histories. Decoding re-resolves every pin (base policy, review policy, risk policy and model identity).</summary>
public sealed class ModelLedgerContextCodec : IContextCodec<ModelLedgerContext>
{
    public static ModelLedgerContextCodec Instance { get; } = new();

    public byte[] EncodeCanonical(ModelLedgerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.EncodeCanonical();
    }

    public ModelLedgerContext Decode(ReadOnlySpan<byte> bytes)
    {
        var r = new CanonicalReader(bytes);
        if (r.ReadInt32() != 1)
        {
            throw new CanonicalFormatException("Unsupported finance V3 context format version.");
        }
        var scenario = r.ReadString();
        var manifest = r.ReadString();
        var reducer = r.ReadString();
        var basePolicy = new PolicyRef(r.ReadString(), r.ReadString(), r.ReadString(), r.ReadString());
        var review = r.ReadString();
        var riskPolicy = r.ReadString();
        var model = new RiskModelIdentity(r.ReadString(), r.ReadString(), r.ReadString(), r.ReadString(), r.ReadString(), 0);
        var normalization = r.ReadString();
        model = model with { ThresholdPpm = r.ReadInt32() };
        r.EnsureEnd();
        if (!string.Equals(normalization, RiskScoreNormalization.NormalizationId, StringComparison.Ordinal))
        {
            throw new CanonicalFormatException($"Unsupported score normalization '{normalization}'.");
        }
        var pinned = ModelLedgerContext.FromPins(scenario, manifest, reducer, basePolicy, review, riskPolicy, model);
        return pinned.TryGetValue(out var context) ? context : throw new CanonicalFormatException(pinned.Error.Message);
    }
}

/// <summary>Canonical complete-state codec of V3 histories: format 1, the V2 state bytes, then the learned screenings in intent-id order.</summary>
public sealed class ModelLedgerStateCodec : IStateCodec<ModelLedgerState>
{
    public const int FormatVersion = 1;
    public static ModelLedgerStateCodec Instance { get; } = new();

    public byte[] EncodeCanonical(ModelLedgerState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var w = new CanonicalWriter().WriteInt32(FormatVersion).WriteBytes(LedgerFinanceStateCodec.Instance.EncodeCanonical(state.Ledger))
            .WriteCount(state.Screenings.Count);
        foreach (var (intentId, s) in state.Screenings)
        {
            w.WriteString(intentId).WriteInt64(s.FactSeq).WriteString(s.EvidenceSha256).WriteString(s.FeatureHash).WriteInt64(s.SourcePostedBeforeMinor)
                .WriteInt32(s.ScorePpm).WriteInt32(s.ThresholdPpm).WriteByte((byte)s.BaseDecision).WriteBoolean(s.LearnedHold).WriteByte((byte)s.FinalDecision);
        }
        return w.ToArray();
    }

    public ModelLedgerState Decode(ReadOnlySpan<byte> bytes)
    {
        var r = new CanonicalReader(bytes);
        if (r.ReadInt32() != FormatVersion)
        {
            throw new CanonicalFormatException("Unsupported finance V3 state format version.");
        }
        var ledger = LedgerFinanceStateCodec.Instance.Decode(r.ReadBytes());
        var screenings = ImmutableSortedDictionary.CreateBuilder<string, LearnedScreening>(StringComparer.Ordinal);
        var count = r.ReadCount();
        for (var i = 0; i < count; i++)
        {
            var intentId = r.ReadString();
            var s = new LearnedScreening(r.ReadInt64(), r.ReadString(), r.ReadString(), r.ReadInt64(), r.ReadInt32(), r.ReadInt32(),
                Decision(r.ReadByte()), r.ReadBoolean(), Decision(r.ReadByte()));
            if (!ledger.Payments.ContainsKey(intentId) || !screenings.TryAdd(intentId, s))
            {
                throw new CanonicalFormatException($"Learned screening '{intentId}' is duplicated or names no payment.");
            }
        }
        r.EnsureEnd();
        return new ModelLedgerState(ledger, screenings.ToImmutable());
    }

    private static ScreeningDecisionKind Decision(byte value)
    {
        var d = (ScreeningDecisionKind)value;
        return Enum.IsDefined(d) ? d : throw new CanonicalFormatException($"Unknown screening decision {value}.");
    }
}

/// <summary>
/// Pinned Timeline version identity of model-aware (V3) durable finance histories and the components registered for it (ADR-P1-20).
/// A separate engine shape beside V1 and V2. A fork inherits this key; no context substitution can switch a V1/V2 history to it.
/// </summary>
public static class ModelLedgerTimelineVersions
{
    public const int CheckpointInterval = 32;

    public static TimelineVersionKey V3 { get; } = new(
        ModelLedgerVersion.DomainId, ModelLedgerVersion.ReducerVersion, ModelLedgerVersion.InputSchemaVersion,
        ModelLedgerVersion.StateSchemaVersion, ModelLedgerVersion.ContextSchemaVersion);

    public static TimelineComponents<ModelLedgerState, FinanceFact, ModelLedgerContext> V3Components { get; } = new(
        V3, ModelLedgerTransition.Instance, ModelLedgerStateCodec.Instance, FinanceFactInputCodec.Instance, ModelLedgerContextCodec.Instance);

    /// <summary>Every V3-shape version a stored model-aware history may pin. Keep old entries when adding new ones.</summary>
    public static IReadOnlyList<TimelineComponents<ModelLedgerState, FinanceFact, ModelLedgerContext>> All { get; } = [V3Components];
}
