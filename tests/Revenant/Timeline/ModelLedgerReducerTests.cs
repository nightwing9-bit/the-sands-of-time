using System.Security.Cryptography;
using Marrow.Kernel.Canonical;
using NetArchTest.Rules;
using Revenant.Cases;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Intelligence;
using Revenant.Scenario;
using Revenant.Timeline;
using Xunit;
using Xunit.Abstractions;

namespace Revenant.Tests.Timeline;

/// <summary>
/// Spec 08 task 8.3: the pure model-aware (V3) finance reducer, its codecs, pins and version registration, without a database.
/// Scores in these tests are either produced by the committed, verified artifact or are explicit integer test inputs for the
/// decision rule (named as such); none is presented as a model output.
/// **Validates: Requirements R08.02, R08.08, R08.10, R08.11**
/// </summary>
public sealed class ModelLedgerReducerTests(ITestOutputHelper output)
{
    private static readonly ScenarioRunId Run = new(Guid.Parse("00000000-0000-0000-0000-0000000083a1"));
    private static readonly RiskPolicy Risk = RiskPolicyCatalog.V1;
    private static readonly int Threshold = Risk.Model.ThresholdPpm;

    private static (ScenarioRun Run, ModelLedgerContext Context, ModelLedgerState State) Start()
    {
        var run = FridayRingScenario.Create(42, ScenarioSchemas.FridayRingVersion, Run).Value;
        var context = ModelLedgerContext.Pin(run.Manifest, Risk).Value;
        return (run, context, ModelLedgerReducer.Initial(run, context).Value);
    }

    private static FinanceFact IntentFact(long seq, PaymentIntent intent) =>
        new(FinanceFactKind.PaymentIntent, seq, ScenarioSchemas.InputSchemaVersion, PaymentIntentCodec.Instance.EncodeCanonical(intent), null);

    private static FinanceFact ScreeningFact(long seq, InferenceEvidenceV1 evidence)
    {
        var bytes = evidence.ToCanonicalBytes();
        return new FinanceFact(FinanceFactKind.Screening, seq, ModelLedgerVersion.ScreeningSchema, bytes,
            new FactEvidenceRef(InferenceEvidenceV1.SchemaId, SHA256.HashData(bytes)));
    }

    /// <summary>Evidence as the live adapter builds it, from state, with an explicit integer score (test input for the decision rule).</summary>
    private static InferenceEvidenceV1 Evidence(ModelLedgerState state, PaymentIntent intent, int scorePpm, ModelLedgerContext context,
        long? postedOverride = null, ScreeningDecisionKind? baseOverride = null, int? thresholdOverride = null, string? modelOverride = null, long? seqOverride = null)
    {
        var p = state.Ledger.Payments[intent.IntentId];
        var posted = postedOverride ?? state.Ledger.Accounts[intent.SourceAccountId].PostedMinor;
        var raw = RiskFeaturesV1.Build(intent.Amount.Currency, intent.Amount.MinorUnits, posted).Value.Raw;
        var asOf = seqOverride ?? state.LastFact;
        var baseDecision = baseOverride ?? ModelLedgerReducer.BaseDecision(FinanceLedgerReducer.ScreeningReason(p.Intent, p.Velocity15m, context.Base.Policy));
        var m = Risk.Model;
        return new InferenceEvidenceV1(intent.RunId, intent.IntentId, asOf, asOf + 1, raw, modelOverride ?? m.ModelId, m.ArtifactSha256, m.ManifestSha256,
            m.MlNetPackageVersion, Risk.PolicyId, thresholdOverride ?? m.ThresholdPpm, scorePpm, baseDecision);
    }

    private static PaymentIntent With(PaymentIntent i, long amount, bool sanctions = false) =>
        new(i.RunId, i.IntentId, i.Sequence, i.EventTimeUtcMs, i.SourceBankId, i.SourceAccountId, i.DestBankId, i.DestAccountId,
            Money.Usd(amount), i.Truth, new PaymentFeatures(i.Features.NewBeneficiary, sanctions));

    private static ModelLedgerState Intake(ModelLedgerState state, PaymentIntent intent, ModelLedgerContext context) =>
        ModelLedgerReducer.Apply(state, IntentFact(state.LastFact + 1, intent), context).Value;

    [Fact]
    public void V3_is_an_additive_version_and_V1_V2_registrations_are_unchanged()
    {
        Assert.Equal("revenant-finance|revenant-finance-reducer/3|revenant-finance-fact/3|revenant-finance-state/3|revenant-finance-context/3",
            $"{ModelLedgerTimelineVersions.V3.DomainId}|{ModelLedgerTimelineVersions.V3.ReducerVersion}|{ModelLedgerTimelineVersions.V3.InputSchemaVersion}|{ModelLedgerTimelineVersions.V3.StateSchemaVersion}|{ModelLedgerTimelineVersions.V3.ContextSchemaVersion}");
        Assert.NotEqual(FinanceLedgerTimelineVersions.V2, ModelLedgerTimelineVersions.V3);
        Assert.NotEqual(FinanceTimelineVersions.V1, ModelLedgerTimelineVersions.V3);
        Assert.Equal([FinanceLedgerTimelineVersions.V2], FinanceLedgerTimelineVersions.All.Select(c => c.Versions));
        Assert.Equal([FinanceTimelineVersions.V1], FinanceTimelineVersions.All.Select(c => c.Versions));
        Assert.Equal([ModelLedgerTimelineVersions.V3], ModelLedgerTimelineVersions.All.Select(c => c.Versions));
        Assert.Equal(Revenant.Payments.PaymentLedgerStore.ModelAwareFinanceVersion, ModelLedgerVersion.ReducerVersion);
        Assert.Equal("revenant-finance-reducer/2", Revenant.Payments.PaymentLedgerStore.FinanceVersion);
        Assert.Equal("v7-base+ml-risk-v1", Risk.PolicyId);
        Assert.Equal(PolicyCatalog.RefOf(PolicyCatalog.V7), Risk.BasePolicy);
    }

    [Fact]
    public void The_pinned_risk_policy_equals_the_identity_the_verifying_loader_reports_for_the_committed_bytes()
    {
        var provider = new RiskModelProvider(RiskModelOptions.DefaultArtifactRoot);
        var scorer = provider.Get(Risk);
        Assert.True(scorer.IsSuccess, scorer.IsSuccess ? "" : scorer.Error.ToString());
        Assert.Equal(Risk.Model, scorer.Value.Identity);
        Assert.Same(scorer.Value, provider.Get(Risk).Value); // loaded once, then cached
        // A missing artifact root is a typed failure, never a fallback.
        Assert.Equal(RiskModelError.ManifestMissing, new RiskModelProvider(Path.Combine(Path.GetTempPath(), $"none-{Guid.NewGuid():N}")).Get(Risk).Error.Code);
        // A verified artifact that is not the pinned one is refused.
        var other = Risk with { Model = Risk.Model with { ThresholdPpm = Threshold + 1 } };
        Assert.Equal(RiskModelError.PinnedIdentityMismatch, provider.Get(other with { PolicyId = "other" }).Error.Code);
    }

    [Theory]
    [InlineData(-1, PaymentStatus.Cleared, PaymentReason.None)]
    [InlineData(0, PaymentStatus.Held, PaymentReason.MlRiskHold)]
    [InlineData(1, PaymentStatus.Held, PaymentReason.MlRiskHold)]
    public void A_base_clear_payment_is_held_exactly_when_the_recorded_score_is_at_or_above_the_threshold(int offset, PaymentStatus status, PaymentReason reason)
    {
        var (run, context, state) = Start();
        var intent = run.Intents[0];
        state = Intake(state, intent, context);
        var evidence = Evidence(state, intent, Threshold + offset, context); // integer test input at the threshold edge
        Assert.Equal(ScreeningDecisionKind.Clear, evidence.BaseDecision);
        state = ModelLedgerReducer.Apply(state, ScreeningFact(2, evidence), context).Value;
        var p = state.Ledger.Payments[intent.IntentId];
        Assert.Equal((status, reason), (p.Status, p.Reason));
        Assert.Equal(status == PaymentStatus.Held ? intent.Amount.MinorUnits : 0, p.ReservedMinor);
        Assert.Equal(status == PaymentStatus.Held ? CaseStatus.Open : null, p.Case);
        Assert.Equal(state.Ledger.Accounts[intent.SourceAccountId].ReservedMinor, p.ReservedMinor);
        var s = state.Screenings[intent.IntentId];
        Assert.Equal((2L, Threshold + offset, offset >= 0), (s.FactSeq, s.ScorePpm, s.LearnedHold));
    }

    [Fact]
    public void Hard_v7_holds_keep_their_reason_and_a_score_never_releases_them()
    {
        var (run, context, state) = Start();
        var intent = With(run.Intents[0], 50_000, sanctions: true);
        state = Intake(state, intent, context);
        var evidence = Evidence(state, intent, 0, context);
        Assert.Equal((ScreeningDecisionKind.Held, ScreeningDecisionKind.Held), (evidence.BaseDecision, evidence.FinalDecision));
        state = ModelLedgerReducer.Apply(state, ScreeningFact(2, evidence), context).Value;
        Assert.Equal((PaymentStatus.Held, PaymentReason.SanctionsHit), (state.Ledger.Payments[intent.IntentId].Status, state.Ledger.Payments[intent.IntentId].Reason));
        // An insufficient-funds learned hold follows the V2 reservation rule: Held -> Rejected(insufficient), no reservation.
        var (run2, context2, s2) = Start();
        var big = With(run2.Intents[0], 6_000_000);
        s2 = Intake(s2, big, context2);
        s2 = ModelLedgerReducer.Apply(s2, ScreeningFact(2, Evidence(s2, big, 1_000_000, context2)), context2).Value;
        var p2 = s2.Ledger.Payments[big.IntentId];
        Assert.Equal((PaymentStatus.Rejected, PaymentReason.InsufficientAvailable, 0L), (p2.Status, p2.Reason, p2.ReservedMinor));
        Assert.Equal([PaymentStatus.Initiated, PaymentStatus.Screened, PaymentStatus.Held, PaymentStatus.Rejected], p2.Trail.ToArray());
    }

    [Fact]
    public void A_screening_without_matching_evidence_pins_or_causal_inputs_is_refused()
    {
        var (run, context, state) = Start();
        var intent = run.Intents[0];
        state = Intake(state, intent, context);
        LedgerFinanceErrorCode Refused(FinanceFact fact) => ModelLedgerReducer.Apply(state, fact, context).Error.Code;
        var good = ScreeningFact(2, Evidence(state, intent, 10, context));

        // A V2 evidence-free screening fact cannot screen a model-required payment.
        Assert.Equal(LedgerFinanceErrorCode.MalformedFact, Refused(new FinanceFact(FinanceFactKind.Screening, 2, PaymentStepCodec.ScreeningSchema, PaymentStepCodec.Encode(intent.IntentId), null)));
        Assert.Equal(LedgerFinanceErrorCode.MalformedFact, Refused(good with { Evidence = null }));
        Assert.Equal(LedgerFinanceErrorCode.MalformedFact, Refused(good with { Evidence = new FactEvidenceRef(InferenceEvidenceV1.SchemaId, new byte[32]) }));
        // Stale or future posted balance, a different model or threshold, a dishonest base decision, another position.
        Assert.Equal(LedgerFinanceErrorCode.InconsistentFact, Refused(ScreeningFact(2, Evidence(state, intent, 10, context, postedOverride: 4_999_999))));
        Assert.Equal(LedgerFinanceErrorCode.VersionMismatch, Refused(ScreeningFact(2, Evidence(state, intent, 10, context, thresholdOverride: Threshold - 1))));
        Assert.Equal(LedgerFinanceErrorCode.VersionMismatch, Refused(ScreeningFact(2, Evidence(state, intent, 10, context, modelOverride: "other-model/1"))));
        Assert.Equal(LedgerFinanceErrorCode.InconsistentFact, Refused(ScreeningFact(2, Evidence(state, intent, 10, context, baseOverride: ScreeningDecisionKind.Held))));
        Assert.Equal(LedgerFinanceErrorCode.InconsistentFact, Refused(ScreeningFact(2, Evidence(state, intent, 10, context, seqOverride: 0)) with { Sequence = 2 }));
        // Evidence of another amount (does not bind to the intent).
        var other = With(intent, intent.Amount.MinorUnits + 1);
        var mismatched = ScreeningFact(2, new InferenceEvidenceV1(Run, intent.IntentId, 1, 2, RiskFeaturesV1.Build("USD", other.Amount.MinorUnits, 5_000_000).Value.Raw,
            Risk.Model.ModelId, Risk.Model.ArtifactSha256, Risk.Model.ManifestSha256, Risk.Model.MlNetPackageVersion, Risk.PolicyId, Threshold, 10, ScreeningDecisionKind.Clear));
        Assert.Equal(LedgerFinanceErrorCode.InconsistentFact, Refused(mismatched));
        // Evidence on a non-screening fact, and a tampered evidence byte.
        Assert.Equal(LedgerFinanceErrorCode.MalformedFact,
            ModelLedgerReducer.Apply(state, IntentFact(2, run.Intents[1]) with { Evidence = good.Evidence }, context).Error.Code);
        var tampered = (byte[])good.Payload.Clone();
        tampered[^1] ^= 0x01;
        Assert.Equal(LedgerFinanceErrorCode.MalformedFact, Refused(good with { Payload = tampered }));
        Assert.True(ModelLedgerReducer.Apply(state, good, context).IsSuccess);
    }

    [Fact]
    public void State_and_context_round_trip_canonically_and_a_changed_pin_fails_closed()
    {
        var (run, context, state) = Start();
        var intent = run.Intents[0];
        state = Intake(state, intent, context);
        state = ModelLedgerReducer.Apply(state, ScreeningFact(2, Evidence(state, intent, Threshold, context)), context).Value;
        var bytes = ModelLedgerStateCodec.Instance.EncodeCanonical(state);
        Assert.Equal(bytes, ModelLedgerStateCodec.Instance.EncodeCanonical(ModelLedgerStateCodec.Instance.Decode(bytes)));
        Assert.Throws<CanonicalFormatException>(() => ModelLedgerStateCodec.Instance.Decode([.. bytes, 0]));

        var ctx = ModelLedgerContextCodec.Instance.EncodeCanonical(context);
        Assert.Equal(ctx, ModelLedgerContextCodec.Instance.EncodeCanonical(ModelLedgerContextCodec.Instance.Decode(ctx)));
        Assert.NotEqual(context.ContentHash, context.Base.ContentHash);
        // The threshold is the last field: any other value is not the pinned policy.
        var changed = (byte[])ctx.Clone();
        changed[^1] ^= 0x01;
        Assert.Throws<CanonicalFormatException>(() => ModelLedgerContextCodec.Instance.Decode(changed));
        // A V2 context cannot be decoded as V3, and the V2 reducer refuses a V3 evidence screening fact.
        Assert.ThrowsAny<Exception>(() => ModelLedgerContextCodec.Instance.Decode(LedgerFinanceContextCodec.Instance.EncodeCanonical(context.Base)));
        var v2 = FinanceLedgerReducer.Apply(FinanceLedgerReducer.Initial(run, context.Base).Value, IntentFact(1, intent), context.Base).Value;
        var (_, _, fresh) = Start();
        fresh = Intake(fresh, intent, context);
        Assert.Equal(LedgerFinanceErrorCode.MalformedFact, FinanceLedgerReducer.Apply(v2, ScreeningFact(2, Evidence(fresh, intent, 1, context)), context.Base).Error.Code);
    }

    [Fact]
    public void The_pure_finance_layer_never_depends_on_ML_NET()
    {
        var result = Types.InAssembly(typeof(ModelLedgerReducer).Assembly).That()
            .ResideInNamespaceStartingWith("Revenant.Timeline").Or().ResideInNamespaceStartingWith("Revenant.Finance")
            .ShouldNot().HaveDependencyOnAny("Microsoft.ML", "Revenant.Payments", "Npgsql").GetResult();
        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    /// <summary>
    /// The real pinned model through the pure fold, Friday Ring seed 42 in the order intake, screening, settlement per intent (each
    /// settlement changes the posted balances later features see). Records base decision, actual scorePpm and final decision. The
    /// table is printed; the assertions are only the decision rule and v7 precedence, not a desired number of holds.
    /// </summary>
    [Fact]
    public void Friday_ring_pure_fold_with_the_real_pinned_model_follows_the_decision_rule()
    {
        var (run, context, state) = Start();
        var scorer = new RiskModelProvider(RiskModelOptions.DefaultArtifactRoot).Get(Risk).Value;
        var seq = 0L;
        var learned = 0;
        foreach (var intent in run.Intents)
        {
            state = ModelLedgerReducer.Apply(state, IntentFact(++seq, intent), context).Value;
            var posted = state.Ledger.Accounts[intent.SourceAccountId].PostedMinor;
            var snapshot = RiskFeaturesV1.Build("USD", intent.Amount.MinorUnits, posted).Value;
            var score = scorer.Score(snapshot.Vector).Value;
            var evidence = Evidence(state, intent, score, context);
            state = ModelLedgerReducer.Apply(state, ScreeningFact(++seq, evidence), context).Value;
            var p = state.Ledger.Payments[intent.IntentId];
            output.WriteLine($"{intent.IntentId} posted {posted} amount {intent.Amount.MinorUnits} base {evidence.BaseDecision} score {score} final {evidence.FinalDecision} -> {p.Status}");
            Assert.Equal(score >= Threshold && evidence.BaseDecision == ScreeningDecisionKind.Clear ? ScreeningDecisionKind.Held : evidence.BaseDecision, evidence.FinalDecision);
            learned += evidence.FinalDecision == ScreeningDecisionKind.Held && evidence.BaseDecision == ScreeningDecisionKind.Clear ? 1 : 0;
            if (p.Status == PaymentStatus.Cleared)
            {
                state = ModelLedgerReducer.Apply(state, new FinanceFact(FinanceFactKind.Settlement, ++seq, PaymentStepCodec.SettlementSchema, PaymentStepCodec.Encode(intent.IntentId), null), context).Value;
            }
        }
        output.WriteLine($"learned holds among {run.Intents.Length} generated payments: {learned}; threshold {Threshold}");
    }
}
