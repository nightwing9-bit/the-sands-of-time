using Marrow.Kernel.Canonical;
using Revenant.Cases;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Observations;
using Revenant.Payments;
using Revenant.Scenario;
using Revenant.Timeline;

namespace Revenant.Counterfactual;

internal enum V4EvaluationTruth { Unknown, Legitimate, Malicious }
internal enum V4ImpactCause { None, FraudControl, IndirectFunding, OtherOperational }
internal sealed record V4PaymentOutcome(PaymentStatus Status, PaymentReason ScreeningReason, PaymentReason FinalReason,
    long? HeldAtMs, long? SettledAtMs, long? RejectedAtMs, long? ReturnedAtMs, long ReservedMinor, long PostedMinor,
    long RecoveredMinor, bool Intercepted, bool Deferred, bool Reviewed, CaseStatus? Case, string? ScreeningEvidenceSha256,
    long? ReviewFact, V4ImpactCause Cause);
internal sealed record V4PaymentComparison(string IntentId, long PrincipalMinor, long AcceptedAtMs, V4PaymentOutcome Source,
    V4PaymentOutcome Alternative, bool AlternativeDelayed, long SourceDelayMinutes, long AlternativeDelayMinutes,
    long InterventionCostDeltaMinor, long DelayCostDeltaMinor, long AvoidedExposureDeltaMinor);
internal sealed record V4OutcomeTotals(int InterceptedCount, long InterceptedMinor, int DeferredCount, long DeferredMinor,
    int MaliciousSettledCount, long MaliciousSettledMinor, int MaliciousOperationalCount, long MaliciousOperationalMinor,
    long RecoveredMinor, long MaliciousRecoveredMinor, int LegitimateInterventionCount, int LegitimateHeldCount,
    int LegitimateRejectedCount, int LegitimatePendingCount, int LegitimateReleasedCount, int DelayedCount, long DelayMinutes,
    long InterventionCostMinor, long DelayCostMinor, long EligibleMaliciousExposureMinor, long NetFinancialPositionMinor);
internal sealed record V4BalanceComparison(long BoundaryMs, string BankId, ObservationBalance Source, ObservationBalance Alternative);
internal sealed record V4EconomicComparison(string Schema, string Currency, CounterfactualCostModel CostPolicy, long HorizonMs,
    V4OutcomeTotals Source, V4OutcomeTotals Alternative, int AlternativeDelayedCount, long AlternativeAdditionalDelayMinutes,
    int UnknownTruthCount, long InterceptedDeltaMinor, long AvoidedExposureMinor, long InterventionCostDeltaMinor,
    long DelayCostDeltaMinor, long NetEffectMinor, IReadOnlyList<V4PaymentComparison> Payments,
    IReadOnlyList<V4BalanceComparison> Balances, string EvidenceDigest);

internal static class V4CounterfactualEconomics
{
    internal const string Schema = "revenant-v4-counterfactual-economics/1";
    private const long Minute = 60_000;
    private sealed record Trace(long? Held = null, long? Settled = null, long? Rejected = null, long? Returned = null,
        PaymentReason Screening = PaymentReason.None, string? Evidence = null, long? Review = null);

    /// <summary>Truth is a separate, retrospective, authorized join. Absent labels stay unknown.</summary>
    internal static V4EconomicComparison Compare(V4ReplayPlan plan, IReadOnlyDictionary<string, V4EvaluationTruth> truth)
    {
        var source = plan.SourceFinal;
        var child = plan.AlternativeFinal;
        var horizon = source.LastSealedBoundaryMs;
        var costs = CounterfactualCostModel.V1;
        if (child.LastSealedBoundaryMs != horizon || source.RunId != child.RunId
            || source.Ledger.ManifestContentHash != child.Ledger.ManifestContentHash
            || source.Ledger.Currency != costs.Currency || child.Ledger.Currency != costs.Currency
            || !source.Ledger.Payments.Keys.SequenceEqual(child.Ledger.Payments.Keys, StringComparer.Ordinal))
            throw new InvalidOperationException("Economic comparison needs the same USD intents and completed horizon.");

        var before = TraceHistory(plan.SourceHistory, source);
        var after = TraceHistory(plan.AlternativeHistory, child);
        var rows = new List<V4PaymentComparison>(before.Count);
        var original = new Totals();
        var alternative = new Totals();
        long additionalMinutes = 0;
        int delayed = 0, unknown = 0;
        var digest = new CanonicalWriter().WriteString(Schema).WriteString(costs.Version).WriteString(costs.Currency)
            .WriteInt64(costs.FalsePositiveCostMinor).WriteInt64(costs.DelayCostPerMinuteMinor).WriteInt32(costs.DelayCapMinutes)
            .WriteInt64(horizon).WriteString(source.PrefixDigest).WriteString(child.PrefixDigest)
            .WriteCount(before.Count);
        checked
        {
            foreach (var (id, p) in source.Ledger.Payments)
            {
                var a = child.Ledger.Payments[id];
                if (p.Intent != a.Intent)
                    throw new InvalidOperationException("Counterfactual altered an upstream payment intent.");
                var label = truth.GetValueOrDefault(id, V4EvaluationTruth.Unknown);
                if (!Enum.IsDefined(label)) throw new InvalidOperationException("Invalid evaluation truth.");
                if (label == V4EvaluationTruth.Unknown) unknown++;
                var b = before[id];
                var c = after[id];
                var principal = p.Intent.Amount.MinorUnits;
                var accepted = p.Intent.EventTimeUtcMs;
                var legitimate = label == V4EvaluationTruth.Legitimate;
                var malicious = label == V4EvaluationTruth.Malicious;
                var comparableDelay = legitimate && (b.SettledAtMs.HasValue || c.SettledAtMs.HasValue);
                var elapsedBefore = comparableDelay ? (b.SettledAtMs ?? horizon) - accepted : 0;
                var elapsedAfter = comparableDelay ? (c.SettledAtMs ?? horizon) - accepted : 0;
                var baseline = Math.Min(elapsedBefore, elapsedAfter);
                var sourceMinutes = (elapsedBefore - baseline) / Minute;
                var altMinutes = (elapsedAfter - baseline) / Minute;
                var delayBefore = Math.Min(sourceMinutes, costs.DelayCapMinutes) * costs.DelayCostPerMinuteMinor;
                var delayAfter = Math.Min(altMinutes, costs.DelayCapMinutes) * costs.DelayCostPerMinuteMinor;
                var isDelayed = legitimate && b.SettledAtMs is { } settled && (c.SettledAtMs is null || c.SettledAtMs > settled);
                if (isDelayed) { delayed++; additionalMinutes += altMinutes; }
                var interventionBefore = legitimate && IsIntervention(b) ? costs.FalsePositiveCostMinor : 0;
                var interventionAfter = legitimate && IsIntervention(c) ? costs.FalsePositiveCostMinor : 0;
                // Matched surviving malicious exposure is monetized only across a terminal interception pair.
                // Pending and funding failures stay in supporting totals; a compensating return is recovery.
                var exposureBefore = malicious && c.Intercepted ? b.PostedMinor - b.RecoveredMinor : 0;
                var exposureAfter = malicious && b.Intercepted ? c.PostedMinor - c.RecoveredMinor : 0;
                original.Add(b, label, principal, elapsedBefore > baseline, sourceMinutes, interventionBefore, delayBefore, exposureBefore);
                alternative.Add(c, label, principal, elapsedAfter > baseline, altMinutes, interventionAfter, delayAfter, exposureAfter);
                rows.Add(new(id, principal, accepted, b, c, isDelayed, sourceMinutes, altMinutes,
                    interventionAfter - interventionBefore, delayAfter - delayBefore, exposureBefore - exposureAfter));
                digest.WriteString(id).WriteByte((byte)label).WriteInt64(principal).WriteInt64(accepted);
                WriteOutcome(digest, b);
                WriteOutcome(digest, c);
                digest.WriteBoolean(isDelayed).WriteInt64(sourceMinutes).WriteInt64(altMinutes)
                    .WriteInt64(interventionAfter - interventionBefore).WriteInt64(delayAfter - delayBefore)
                    .WriteInt64(exposureBefore - exposureAfter);
            }
            var balances = Align(plan.SourceHistory, plan.AlternativeHistory);
            digest.WriteCount(balances.Length);
            foreach (var point in balances)
            {
                digest.WriteInt64(point.BoundaryMs).WriteString(point.BankId);
                point.Source.Write(digest);
                point.Alternative.Write(digest);
            }
            var o = original.Result();
            var n = alternative.Result();
            return new(Schema, costs.Currency, costs, horizon, o, n, delayed, additionalMinutes, unknown,
                n.InterceptedMinor - o.InterceptedMinor, o.EligibleMaliciousExposureMinor - n.EligibleMaliciousExposureMinor,
                n.InterventionCostMinor - o.InterventionCostMinor, n.DelayCostMinor - o.DelayCostMinor,
                n.NetFinancialPositionMinor - o.NetFinancialPositionMinor, rows.AsReadOnly(), balances, ObservedFinanceEncoding.Hash(digest));
        }
    }

    private static bool IsIntervention(V4PaymentOutcome p) => p.HeldAtMs.HasValue || p.Intercepted;

    private static Dictionary<string, V4PaymentOutcome> TraceHistory(V4ProjectionHistory history, ObservedFinanceState final)
    {
        var state = history.Genesis;
        var traces = new Dictionary<string, Trace>(StringComparer.Ordinal);
        foreach (var segment in history.Segments)
        foreach (var row in segment.Facts)
        {
            var input = row.Input;
            var next = row.Apply(state, segment.Context);
            if (input.Kind != ObservedFactKind.ObservationSeal)
            {
                var id = input.Kind switch
                {
                    ObservedFactKind.PaymentIntent => PaymentIntentCodec.Instance.Decode(input.Payload.AsSpan()).IntentId,
                    ObservedFactKind.Screening => V4ModelScreeningEvidence.Decode(input).IntentId,
                    ObservedFactKind.Settlement => PaymentStepCodec.Decode(input.Payload.AsSpan()),
                    ObservedFactKind.ReviewDecision => ReviewDecisionCodec.Decode(input.Payload.AsSpan()).IntentId,
                    ObservedFactKind.ReturnInstruction => ReturnInstructionCodec.Decode(input.Payload.AsSpan()).IntentId,
                    _ => throw new InvalidOperationException("Unexpected comparison fact.")
                };
                var p = next.Ledger.Payments[id];
                var t = traces.GetValueOrDefault(id) ?? new();
                traces[id] = t with
                {
                    Held = t.Held ?? (p.Status == PaymentStatus.Held ? input.EffectiveTimeMs : null),
                    Settled = t.Settled ?? (p.SettlementPosted ? input.EffectiveTimeMs : null),
                    Rejected = t.Rejected ?? (p.Status == PaymentStatus.Rejected ? input.EffectiveTimeMs : null),
                    Returned = t.Returned ?? (p.Status == PaymentStatus.Returned ? input.EffectiveTimeMs : null),
                    Screening = input.Kind == ObservedFactKind.Screening ? p.Reason : t.Screening,
                    Evidence = input.Kind == ObservedFactKind.Screening ? input.EvidenceSha256 : t.Evidence,
                    Review = input.Kind == ObservedFactKind.ReviewDecision ? input.Sequence : t.Review
                };
            }
            state = next;
        }
        if (!V4Counterfactual.SameState(state, final))
            throw new InvalidOperationException("Economic inputs do not match the executed comparison.");
        return final.Ledger.Payments.ToDictionary(pair => pair.Key, pair =>
        {
            var p = pair.Value;
            var t = traces[pair.Key];
            var intercepted = !p.SettlementPosted && p.Status == PaymentStatus.Rejected && p.Case == CaseStatus.Rejected
                && t.Review.HasValue && t.Screening is PaymentReason.SanctionsHit or PaymentReason.VelocityRule
                    or PaymentReason.NewBeneficiaryAmountRule or PaymentReason.MlRiskHold;
            var deferred = p.Status == PaymentStatus.Held && p.Case is CaseStatus.Open or CaseStatus.AwaitingSecondApproval;
            var cause = p.Reason == PaymentReason.InsufficientAvailable ? V4ImpactCause.IndirectFunding
                : t.Held.HasValue || intercepted ? V4ImpactCause.FraudControl
                : p.Status is PaymentStatus.Rejected or PaymentStatus.Returned ? V4ImpactCause.OtherOperational : V4ImpactCause.None;
            return new V4PaymentOutcome(p.Status, t.Screening, p.Reason, t.Held, t.Settled, t.Rejected, t.Returned,
                p.ReservedMinor, p.SettlementPosted ? p.Intent.Amount.MinorUnits : 0, p.ReturnPosted ? p.Intent.Amount.MinorUnits : 0,
                intercepted, deferred, t.Review.HasValue, p.Case, t.Evidence, t.Review, cause);
        }, StringComparer.Ordinal);
    }

    private static V4BalanceComparison[] Align(V4ProjectionHistory original, V4ProjectionHistory alternative)
    {
        var source = V4ObservationProjector.Project(original).Banks;
        var child = V4ObservationProjector.Project(alternative).Banks
            .ToDictionary(x => (x.Seal.BoundaryMs, x.BankId));
        if (source.Count != child.Count)
            throw new InvalidOperationException("The comparison curves do not cover the same completed boundaries.");
        return source.Select(x => new V4BalanceComparison(x.Seal.BoundaryMs, x.BankId, x.Balance,
            child[(x.Seal.BoundaryMs, x.BankId)].Balance)).ToArray();
    }

    private static void WriteOutcome(CanonicalWriter w, V4PaymentOutcome p) => w.WriteString(p.Status.ToString())
        .WriteString(p.ScreeningReason.ToString()).WriteString(p.FinalReason.ToString()).WriteInt64(p.HeldAtMs ?? -1)
        .WriteInt64(p.SettledAtMs ?? -1).WriteInt64(p.RejectedAtMs ?? -1).WriteInt64(p.ReturnedAtMs ?? -1)
        .WriteInt64(p.ReservedMinor).WriteInt64(p.PostedMinor).WriteInt64(p.RecoveredMinor).WriteBoolean(p.Intercepted)
        .WriteBoolean(p.Deferred).WriteBoolean(p.Reviewed).WriteString(p.Case?.ToString() ?? "")
        .WriteString(p.ScreeningEvidenceSha256 ?? "").WriteInt64(p.ReviewFact ?? 0).WriteString(p.Cause.ToString());

    private sealed class Totals
    {
        private int intercepted, deferred, settled, operational, intervened, held, rejected, pending, released, delayed;
        private long interceptedMinor, deferredMinor, settledMinor, operationalMinor, recovered, maliciousRecovered,
            minutes, interventionCost, delayCost, exposure;

        internal void Add(V4PaymentOutcome p, V4EvaluationTruth label, long principal, bool later, long delayMinutes,
            long intervention, long delay, long eligibleExposure)
        {
            checked
            {
                recovered += p.RecoveredMinor;
                if (label == V4EvaluationTruth.Malicious)
                {
                    if (p.Intercepted) { intercepted++; interceptedMinor += principal; }
                    if (p.Deferred) { deferred++; deferredMinor += principal; }
                    if (p.PostedMinor > 0) { settled++; settledMinor += p.PostedMinor; }
                    if (p.PostedMinor == 0 && !p.Intercepted && p.Status is PaymentStatus.Rejected or PaymentStatus.Returned)
                    { operational++; operationalMinor += principal; }
                    maliciousRecovered += p.RecoveredMinor;
                }
                if (label == V4EvaluationTruth.Legitimate)
                {
                    if (IsIntervention(p)) intervened++;
                    if (p.HeldAtMs.HasValue) held++;
                    if (p.Intercepted) rejected++;
                    if (p.Deferred) pending++;
                    if (p.Case == CaseStatus.Released) released++;
                    if (later) delayed++;
                }
                minutes += delayMinutes;
                interventionCost += intervention;
                delayCost += delay;
                exposure += eligibleExposure;
            }
        }

        internal V4OutcomeTotals Result() => new(intercepted, interceptedMinor, deferred, deferredMinor, settled, settledMinor,
            operational, operationalMinor, recovered, maliciousRecovered, intervened, held, rejected, pending, released, delayed,
            minutes, interventionCost, delayCost, exposure, checked(-exposure - interventionCost - delayCost));
    }
}
