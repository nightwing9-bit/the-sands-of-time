using System.Collections.Immutable;
using Revenant.Finance;
using Revenant.Intelligence;
using Revenant.Timeline;
using static Revenant.Intelligence.RiskFeatureVectorV2;

namespace Revenant.Observations;

internal sealed record V4RiskAsOf(string IntentId, string SourceBankId, long EffectiveTimeMs, long CutoffFactSeq,
    string CutoffPrefixDigest, RiskFeatureVectorV2 Vector);

internal sealed partial class V4ObservationProjector
{
    private readonly List<V4RiskAsOf> riskRows = [];
    internal IReadOnlyList<V4RiskAsOf> RiskAsOf => riskRows;

    /// <summary>Build from a verified branch prefix ending immediately before the candidate's Screening fact.</summary>
    internal static V4RiskAsOf RiskAt(V4ProjectionHistory canonicalPrefix, string acceptedIntentId, long screeningTimeMs)
    {
        var projector = Project(canonicalPrefix);
        return projector.BuildRisk(projector.prefix, acceptedIntentId, screeningTimeMs);
    }

    private V4RiskAsOf BuildRisk(ObservedFinanceState state, string id, long at)
    {
        if (!state.Ledger.Payments.TryGetValue(id, out var payment) || payment.Status != PaymentStatus.Initiated
            || payment.IntentFact > state.LastFact || at < state.LastEffectiveTimeMs
            || at >= ObservedFinanceVersion.NextBoundary(state.LastSealedBoundaryMs))
            throw new InvalidOperationException("Expected a legal prefix immediately before Screening.");

        var p = payment.Intent;
        var source = PriorActivity(p.SourceAccountId, at, state.LastFact, id);
        var inputs = FraudInputs(state, p, at, source);
        if (inputs.Currency != "USD") throw new InvalidOperationException("The risk schema supports USD only.");
        var balance = inputs.SourceBalance;
        var lastDay = inputs.Windows[^1];
        var v = ImmutableArray.CreateBuilder<int>(Columns.Length);
        v.AddRange((ReadOnlySpan<int>)[Minor(inputs.AmountMinor), 1, Ratio(inputs.AmountMinor, balance.Available), Minor(balance.Posted),
            Minor(balance.Reserved), Minor(balance.Available), Flag(inputs.ReportedNewBeneficiary), Flag(balance.Available == 0),
            Flag(inputs.BeneficiarySeenBefore), Flag(inputs.CounterpartySeenBefore), Age(inputs.SinceOutgoingMs),
            Flag(inputs.SinceOutgoingMs is null), Ratio((Int128)inputs.AmountMinor * lastDay.SubmittedCount, lastDay.SubmittedMinor),
            Flag(lastDay.SubmittedCount == 0)]);
        foreach (var w in inputs.Windows)
        {
            v.AddRange((ReadOnlySpan<int>)[Flag(w.Coverage == ObservationCoverage.Complete), Count(w.SubmittedCount), Minor(w.SubmittedMinor),
                Count(w.OutgoingCount), Minor(w.OutgoingMinor), Count(w.IncomingCount), Minor(w.IncomingMinor),
                Count(w.HeldCount), Count(w.ReturnInCount), Count(w.ReturnOutCount), Count(w.Counterparties), Count(w.Beneficiaries)]);
        }

        var start = at - V4ActivityWindow.Durations[1];
        var lastIn = source.LastOrDefault(x => x.Kind == ActivityKind.SettleIn);
        var settled = source.Where(x => x.At >= start && x.Kind is ActivityKind.SettleIn or ActivityKind.SettleOut).ToArray();
        var destination = PriorActivity(p.DestAccountId, at, state.LastFact, id)
            .Where(x => x.At >= start && x.Kind == ActivityKind.SettleIn).ToArray();
        var recentIncoming = lastIn is not null && lastIn.At >= start ? lastIn : null;
        var forwarded = recentIncoming is null ? 0 : Ratio(Math.Min(recentIncoming.Amount, inputs.AmountMinor),
            Math.Max(recentIncoming.Amount, inputs.AmountMinor));
        v.AddRange((ReadOnlySpan<int>)[Age(lastIn is null ? null : at - lastIn.At), Flag(lastIn is null), Count(destination.LongLength),
            Count(destination.Select(x => x.Other).Distinct(StringComparer.Ordinal).LongCount()), Minor(destination.Sum(x => x.Amount)),
            Ratio(settled.LongCount(x => state.Ledger.Accounts[x.Other].BankId != p.SourceBankId), settled.LongLength),
            forwarded, Flag(recentIncoming is null)]);
        return new(id, p.SourceBankId, at, state.LastFact, state.PrefixDigest, new(v.MoveToImmutable()));
    }
}
