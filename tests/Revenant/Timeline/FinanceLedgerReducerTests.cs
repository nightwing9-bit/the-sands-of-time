using FsCheck;
using FsCheck.Fluent;
using Marrow.Kernel.Canonical;
using Revenant.Cases;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Scenario;
using Revenant.Timeline;
using Xunit;

namespace Revenant.Tests.Timeline;

/// <summary>
/// Spec 07 task 7.5: the pure V2 finance reducer, its codecs and its version registration, without a database.
/// **Validates: Requirements R07.01, R07.02, R07.03, R07.04, R07.11**
/// </summary>
public sealed class FinanceLedgerReducerTests
{
    private static readonly ScenarioRunId Run = new(Guid.Parse("00000000-0000-0000-0000-0000000075a1"));

    /// <summary>
    /// Property (design testing strategy: "sum of six banks assets remains conserved for same-currency balanced groups"): for any
    /// legal stream of intake, screening, review, settlement and return facts, the six banks' posted total equals the opening total,
    /// account reservations equal the payments' own reservations, no available balance goes negative, every trail is a legal walk of
    /// the frozen lifecycle, and the canonical state bytes round-trip.
    /// **Validates: Requirements R07.01, R07.04, R07.11**
    /// </summary>
    [Fact]
    public void Any_legal_fact_stream_conserves_the_six_banks_and_round_trips()
    {
        var gen = from seed in Gen.Choose(1, 500)
                  from length in Gen.Choose(0, 160)
                  from ops in Gen.ArrayOf(Gen.Choose(0, 999), length)
                  select (seed, ops);
        Prop.ForAll(gen.ToArbitrary(), input =>
        {
            var (run, context, state) = Start(input.seed);
            var opening = state.TotalPostedMinor;
            var next = 0;
            foreach (var c in input.ops)
            {
                var fact = NextFact(state, run, context, c, ref next);
                if (fact is null)
                {
                    continue;
                }
                var applied = FinanceLedgerReducer.Apply(state, fact, context);
                if (!applied.TryGetValue(out var after))
                {
                    return false;
                }
                state = after;
                if (state.TotalPostedMinor != opening
                    || state.Accounts.Values.Sum(a => a.ReservedMinor) != state.Payments.Values.Sum(p => p.ReservedMinor)
                    || state.Accounts.Values.Any(a => a.AvailableMinor < 0 || a.ReservedMinor < 0))
                {
                    return false;
                }
            }
            var bytes = LedgerFinanceStateCodec.Instance.EncodeCanonical(state);
            return state.Payments.Values.All(p => PaymentLifecycle.IsLegalTrail(p.Trail))
                && bytes.AsSpan().SequenceEqual(LedgerFinanceStateCodec.Instance.EncodeCanonical(LedgerFinanceStateCodec.Instance.Decode(bytes)));
        }).Check(Config.QuickThrowOnFailure.WithMaxTest(60));
    }

    [Fact]
    public void Screening_holds_reserves_and_settlement_consumes_the_reservation_once()
    {
        var (run, context, state) = Start(42);
        var intent = Sanctioned(run.Intents[0], 400_000);
        var opening = state.Accounts[intent.SourceAccountId].PostedMinor;
        state = Apply(state, Intake(1, intent), context);
        state = Apply(state, Step(2, FinanceFactKind.Screening, intent.IntentId), context);
        var p = state.Payments[intent.IntentId];
        Assert.Equal((PaymentStatus.Held, PaymentReason.SanctionsHit, 400_000L, CaseStatus.Open, 1), (p.Status, p.Reason, p.ReservedMinor, p.Case, p.RequiredApprovals));
        var source = state.Accounts[intent.SourceAccountId];
        Assert.Equal((opening, 400_000L), (source.PostedMinor, source.ReservedMinor)); // posted unchanged while held
        // Settlement of a still-held payment is not a legal step.
        Assert.Equal(LedgerFinanceErrorCode.IllegalStep, FinanceLedgerReducer.Apply(state, Step(3, FinanceFactKind.Settlement, intent.IntentId), context).Error.Code);
        state = Apply(state, Review(3, intent.IntentId, ReviewDecisionAction.Approve, 1, 1, "Released"), context);
        state = Apply(state, Step(4, FinanceFactKind.Settlement, intent.IntentId), context);
        p = state.Payments[intent.IntentId];
        Assert.Equal("Initiated>Screened>Held>Approved>Cleared>Settled", string.Join(">", p.Trail));
        Assert.Equal((opening - 400_000L, 0L), (state.Accounts[intent.SourceAccountId].PostedMinor, state.Accounts[intent.SourceAccountId].ReservedMinor));
        state = Apply(state, Return(5, intent.IntentId), context);
        Assert.Equal(opening, state.Accounts[intent.SourceAccountId].PostedMinor);
        Assert.True(state.Payments[intent.IntentId].ReturnPosted);
    }

    [Fact]
    public void Out_of_order_mismatched_and_inconsistent_facts_fail_closed()
    {
        var (run, context, state) = Start(42);
        var intent = Sanctioned(run.Intents[0], 2_000_000);
        Assert.Equal(LedgerFinanceErrorCode.OutOfOrderFact, FinanceLedgerReducer.Apply(state, Intake(2, intent), context).Error.Code);
        var wrongSchema = new FinanceFact(FinanceFactKind.PaymentIntent, 1, "revenant-payment-intent/9", PaymentIntentCodec.Instance.EncodeCanonical(intent), null);
        Assert.Equal(LedgerFinanceErrorCode.MalformedFact, FinanceLedgerReducer.Apply(state, wrongSchema, context).Error.Code);
        state = Apply(state, Intake(1, intent), context);
        Assert.Equal(LedgerFinanceErrorCode.ConflictingDuplicate, FinanceLedgerReducer.Apply(state, Intake(2, intent), context).Error.Code);
        state = Apply(state, Step(2, FinanceFactKind.Screening, intent.IntentId), context);
        Assert.Equal(2, state.Payments[intent.IntentId].RequiredApprovals); // >= USD 10,000.00
        // A review fact that claims release after one of two approvals contradicts the pinned review policy.
        Assert.Equal(LedgerFinanceErrorCode.InconsistentFact,
            FinanceLedgerReducer.Apply(state, Review(3, intent.IntentId, ReviewDecisionAction.Approve, 1, 2, "Released"), context).Error.Code);
        Assert.Equal(LedgerFinanceErrorCode.InconsistentFact,
            FinanceLedgerReducer.Apply(state, Review(3, intent.IntentId, ReviewDecisionAction.Approve, 1, 1, "Released"), context).Error.Code);
        var other = LedgerFinanceContext.FromPins(context.ScenarioVersion, "other-manifest", FinanceLedgerVersion.ReducerVersion,
            context.PolicyRef, ReviewPolicy.Version).Value;
        Assert.Equal(LedgerFinanceErrorCode.VersionMismatch, FinanceLedgerReducer.Apply(state, Step(3, FinanceFactKind.Settlement, intent.IntentId), other).Error.Code);
        Assert.True(LedgerFinanceContext.FromPins(context.ScenarioVersion, context.ManifestContentHash, FinanceReducer.Version, context.PolicyRef, ReviewPolicy.Version).IsSuccess is false);
    }

    [Fact]
    public void V2_is_registered_beside_an_unchanged_V1_and_its_context_round_trips()
    {
        Assert.Equal([FinanceTimelineVersions.V1], FinanceTimelineVersions.All.Select(c => c.Versions));
        Assert.Equal([FinanceLedgerTimelineVersions.V2], FinanceLedgerTimelineVersions.All.Select(c => c.Versions));
        Assert.NotEqual(FinanceTimelineVersions.V1, FinanceLedgerTimelineVersions.V2);
        Assert.Equal(FinanceTimelineVersions.V1.DomainId, FinanceLedgerTimelineVersions.V2.DomainId);
        var (_, context, _) = Start(7);
        var bytes = LedgerFinanceContextCodec.Instance.EncodeCanonical(context);
        Assert.Equal(context.ContentHash, LedgerFinanceContextCodec.Instance.Decode(bytes).ContentHash);
        Assert.Throws<CanonicalFormatException>(() => LedgerFinanceContextCodec.Instance.Decode([.. bytes, 0]));
        // The fact kind set is frozen with the first V2 history.
        Assert.Equal([1, 2, 3, 4, 5], Enum.GetValues<FinanceFactKind>().Select(k => (int)k));
        Assert.Equal("x-1", PaymentStepCodec.Decode(PaymentStepCodec.Encode("x-1")));
    }

    // ---------------------------------------------------------------------------------------------------------

    private static (ScenarioRun Run, LedgerFinanceContext Context, LedgerFinanceState State) Start(long seed)
    {
        var run = FridayRingScenario.Create(seed, ScenarioSchemas.FridayRingVersion, Run).Value;
        var context = LedgerFinanceContext.Pin(run.Manifest, PolicyCatalog.RefOf(LedgerFinanceContext.LivePolicy)).Value;
        return (run, context, FinanceLedgerReducer.Initial(run, context).Value);
    }

    /// <summary>Chooses the next legal fact from <paramref name="c"/> (kind = c % 5), or null when that kind has nothing to act on.</summary>
    private static FinanceFact? NextFact(LedgerFinanceState state, ScenarioRun run, LedgerFinanceContext context, int c, ref int next)
    {
        var seq = state.LastFact + 1;
        LedgerPayment? First(Func<LedgerPayment, bool> predicate) =>
            state.Payments.Values.Where(predicate).OrderBy(p => p.IntentFact).FirstOrDefault();
        switch (c % 5)
        {
            case 0:
                if (next >= run.Intents.Length)
                {
                    return null;
                }
                var intent = run.Intents[next++];
                return Intake(seq, c / 5 % 3 == 0 ? Sanctioned(intent, intent.Amount.MinorUnits) : intent);
            case 1:
                return First(p => p.Status == PaymentStatus.Initiated) is { } initiated ? Step(seq, FinanceFactKind.Screening, initiated.Intent.IntentId) : null;
            case 2:
                if (First(p => p.Status == PaymentStatus.Held && p.Case is CaseStatus.Open or CaseStatus.AwaitingSecondApproval) is not { } held)
                {
                    return null;
                }
                if (c / 5 % 2 == 0)
                {
                    return Review(seq, held.Intent.IntentId, ReviewDecisionAction.Reject, 0, held.RequiredApprovals, nameof(CaseStatus.Rejected));
                }
                var order = held.Approvals + 1;
                return Review(seq, held.Intent.IntentId, ReviewDecisionAction.Approve, order, held.RequiredApprovals,
                    order == held.RequiredApprovals ? nameof(CaseStatus.Released) : nameof(CaseStatus.AwaitingSecondApproval));
            case 3:
                return First(p => p.Status is PaymentStatus.Approved or PaymentStatus.Cleared) is { } ready
                    ? Step(seq, FinanceFactKind.Settlement, ready.Intent.IntentId) : null;
            default:
                // The live ledger refuses a return the destination cannot cover and records no fact; so does this driver.
                return First(p => p.Status == PaymentStatus.Settled && state.Accounts[p.Intent.DestAccountId].AvailableMinor >= p.Intent.Amount.MinorUnits)
                    is { } settled ? Return(seq, settled.Intent.IntentId) : null;
        }
    }

    private static PaymentIntent Sanctioned(PaymentIntent i, long amount) => new(
        i.RunId, i.IntentId, i.Sequence, i.EventTimeUtcMs, i.SourceBankId, i.SourceAccountId, i.DestBankId, i.DestAccountId,
        new Money(i.Amount.Currency, amount), i.Truth, new PaymentFeatures(i.Features.NewBeneficiary, SanctionsHit: true));

    private static LedgerFinanceState Apply(LedgerFinanceState state, FinanceFact fact, LedgerFinanceContext context)
    {
        var next = FinanceLedgerReducer.Apply(state, fact, context);
        Assert.True(next.IsSuccess, next.IsSuccess ? "" : next.Error.ToString());
        return next.Value;
    }

    private static FinanceFact Intake(long seq, PaymentIntent intent) =>
        new(FinanceFactKind.PaymentIntent, seq, ScenarioSchemas.InputSchemaVersion, PaymentIntentCodec.Instance.EncodeCanonical(intent), null);

    private static FinanceFact Step(long seq, FinanceFactKind kind, string intentId) =>
        new(kind, seq, kind == FinanceFactKind.Screening ? PaymentStepCodec.ScreeningSchema : PaymentStepCodec.SettlementSchema,
            PaymentStepCodec.Encode(intentId), null);

    private static FinanceFact Review(long seq, string intentId, ReviewDecisionAction action, int order, int required, string result) =>
        new(FinanceFactKind.ReviewDecision, seq, ReviewDecisionCodec.SchemaVersion,
            ReviewDecisionCodec.Encode(new ReviewDecisionFact(intentId, action, order, required, result, ReviewPolicy.Version)), null);

    private static FinanceFact Return(long seq, string intentId) =>
        new(FinanceFactKind.ReturnInstruction, seq, ReturnInstructionCodec.SchemaVersion,
            ReturnInstructionCodec.Encode(new ReturnInstructionFact(intentId, "recall")), null);
}
