using System.Collections.Immutable;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Timeline;

namespace Revenant.Observations;

internal sealed partial class V4ObservationProjector
{
    private enum ActivityKind { Intent, Hold, SettleIn, SettleOut, ReturnIn, ReturnOut, Review, Screening, UnpostedSettlement }
    private sealed record Activity(long At, long Position, string Intent, string Other, long Amount, ActivityKind Kind);

    private readonly Dictionary<string, List<Activity>> accountHistory = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<V4AccountObservation>> sparseByAccount = new(StringComparer.Ordinal);
    private readonly SortedSet<string> touched = new(StringComparer.Ordinal);
    private readonly Dictionary<long, V4SealIdentity> seals = [];
    private readonly List<V4AccountObservation> accountRows = [];
    private readonly List<V4FraudAsOfObservation> screeningRows = [];
    private ImmutableSortedDictionary<string, LedgerAccount> accountsAtOpening = null!;
    private ScenarioRunId executionRun;
    private long origin;

    internal IReadOnlyList<V4AccountObservation> Accounts => accountRows;
    internal IReadOnlyList<V4FraudAsOfObservation> FraudAsOf => screeningRows;

    partial void Begin(V4ProjectionHistory history, ObservedFinanceState opening)
    {
        executionRun = history.ExecutionRunId;
        origin = opening.LastSealedBoundaryMs;
        accountsAtOpening = opening.Ledger.Accounts;
    }

    partial void ObserveAccounts(ObservedFinanceInput fact, ObservedFinanceState state, LedgerPayment payment, LedgerPayment? prior)
    {
        var intent = payment.Intent;
        var sender = intent.SourceAccountId;
        var receiver = intent.DestAccountId;
        if (fact.Kind == ObservedFactKind.Screening)
        {
            if (prior is null || prior.IntentFact > state.LastFact || state.LastFact + 1 != fact.Sequence)
            {
                throw new InvalidOperationException("The pre-screening prefix lacks the candidate intake.");
            }
            var candidate = prior.Intent;
            var past = PriorActivity(candidate.SourceAccountId, fact.EffectiveTimeMs, state.LastFact, candidate.IntentId);
            var account = state.Ledger.Accounts[candidate.SourceAccountId];
            var lastOutgoing = past.LastOrDefault(x => x.Kind is ActivityKind.Intent or ActivityKind.SettleOut);
            var utc = DateTimeOffset.FromUnixTimeMilliseconds(fact.EffectiveTimeMs);
            screeningRows.Add(new(executionRun, candidate.IntentId, fact.EffectiveTimeMs, state.LastFact,
                state.LastSealedBoundaryMs, state.PrefixDigest,
                new(candidate.Amount.MinorUnits, candidate.Amount.Currency, new(account.PostedMinor, account.ReservedMinor),
                    candidate.Features.NewBeneficiary, candidate.Features.SanctionsHit,
                    past.Any(x => x.Other == candidate.DestAccountId && x.Kind == ActivityKind.Intent),
                    past.Any(x => x.Other == candidate.DestAccountId),
                    lastOutgoing is null ? null : checked(fact.EffectiveTimeMs - lastOutgoing.At),
                    utc.Hour * 60 + utc.Minute, (int)utc.DayOfWeek, Windows(past, fact.EffectiveTimeMs))));
        }

        switch (fact.Kind)
        {
            case ObservedFactKind.PaymentIntent:
                Add(sender, receiver, ActivityKind.Intent);
                break;
            case ObservedFactKind.Screening:
                Add(sender, receiver, payment.Status == PaymentStatus.Held ? ActivityKind.Hold : ActivityKind.Screening);
                break;
            case ObservedFactKind.ReviewDecision:
                Add(sender, receiver, ActivityKind.Review);
                break;
            case ObservedFactKind.Settlement when prior is { SettlementPosted: false } && payment.SettlementPosted:
                Add(sender, receiver, ActivityKind.SettleOut);
                Add(receiver, sender, ActivityKind.SettleIn);
                break;
            case ObservedFactKind.Settlement:
                Add(sender, receiver, ActivityKind.UnpostedSettlement);
                break;
            case ObservedFactKind.ReturnInstruction when prior is { ReturnPosted: false } && payment.ReturnPosted:
                Add(sender, receiver, ActivityKind.ReturnIn);
                Add(receiver, sender, ActivityKind.ReturnOut);
                break;
        }

        void Add(string id, string other, ActivityKind kind)
        {
            if (!accountHistory.TryGetValue(id, out var entries))
            {
                accountHistory.Add(id, entries = []);
            }
            entries.Add(new(fact.EffectiveTimeMs, fact.Sequence, intent.IntentId, other, intent.Amount.MinorUnits, kind));
            touched.Add(id);
        }
    }

    partial void CloseAccountInterval(V4SealIdentity seal, ObservedFinanceState state)
    {
        seals.Add(seal.BoundaryMs, seal);
        foreach (var id in touched)
        {
            var account = state.Ledger.Accounts[id];
            var row = AccountAt(seal, id, account.BankId, new(account.PostedMinor, account.ReservedMinor));
            if (!sparseByAccount.TryGetValue(id, out var snapshots))
            {
                sparseByAccount.Add(id, snapshots = []);
            }
            snapshots.Add(row);
            accountRows.Add(row);
        }
        touched.Clear();
    }

    internal V4AccountObservation? AccountAsOf(string accountId, long boundary)
    {
        if (!seals.TryGetValue(boundary, out var seal) || !accountsAtOpening.TryGetValue(accountId, out var opening))
        {
            return null;
        }
        var snapshot = sparseByAccount.GetValueOrDefault(accountId)?.LastOrDefault(row => row.Seal.BoundaryMs <= boundary);
        return snapshot?.Seal.BoundaryMs == boundary ? snapshot
            : AccountAt(seal, accountId, opening.BankId, snapshot?.Balance ?? new(opening.PostedMinor, opening.ReservedMinor));
    }

    private V4AccountObservation AccountAt(V4SealIdentity seal, string id, string bank, ObservationBalance balance)
    {
        var prior = PriorActivity(id, seal.BoundaryMs, seal.FactSequence - 1, null);
        var lastSuccess = prior.LastOrDefault(x => x.Kind is ActivityKind.SettleIn or ActivityKind.SettleOut);
        return new(seal, id, bank, balance, Windows(prior, seal.BoundaryMs), lastSuccess?.At,
            prior.Length == 0 ? null : checked(seal.BoundaryMs - prior[^1].At));
    }

    private Activity[] PriorActivity(string id, long time, long position, string? excluded) =>
        accountHistory.TryGetValue(id, out var entries)
            ? entries.TakeWhile(x => x.Position <= position).Where(x => x.At <= time && x.Intent != excluded).ToArray()
            : [];

    private ImmutableArray<V4ActivityWindow> Windows(Activity[] history, long time)
    {
        var elapsed = checked(time - origin);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var novelPositions = history.Where(x => x.Kind == ActivityKind.Intent && seen.Add(x.Other))
            .Select(x => x.Position).ToHashSet();
        return [.. V4ActivityWindow.Durations.Select(duration =>
        {
            var start = elapsed >= duration ? checked(time - duration) : origin;
            var entries = history.Where(x => x.At >= start).ToArray();
            var submitted = entries.Where(x => x.Kind == ActivityKind.Intent).ToArray();
            var incoming = Of(ActivityKind.SettleIn);
            var outgoing = Of(ActivityKind.SettleOut);
            var held = Of(ActivityKind.Hold);
            var returnIn = Of(ActivityKind.ReturnIn);
            var returnOut = Of(ActivityKind.ReturnOut);
            return new V4ActivityWindow(duration, elapsed >= duration ? ObservationCoverage.Complete : ObservationCoverage.NotEnoughHistory,
                incoming.Count, incoming.Amount, outgoing.Count, outgoing.Amount, submitted.LongLength, submitted.Sum(x => x.Amount),
                held.Count, held.Amount, returnIn.Count, returnIn.Amount, returnOut.Count, returnOut.Amount,
                entries.Select(x => x.Other).Distinct(StringComparer.Ordinal).LongCount(),
                submitted.Select(x => x.Other).Distinct(StringComparer.Ordinal).LongCount(),
                submitted.LongCount(x => novelPositions.Contains(x.Position)), submitted.LongCount(x => !novelPositions.Contains(x.Position)));

            (long Count, long Amount) Of(ActivityKind kind)
            {
                var matching = entries.Where(x => x.Kind == kind).ToArray();
                return (matching.LongLength, matching.Sum(x => x.Amount));
            }
        })];
    }
}
