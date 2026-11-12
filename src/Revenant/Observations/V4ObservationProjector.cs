using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Payments;
using Revenant.Scenario;
using Revenant.Timeline;

namespace Revenant.Observations;

internal sealed record V4ProjectionSegment(ObservedFinanceContext Context, IReadOnlyList<V4StoredFact> Facts,
    long LastFact, V4ForkOrigin? Opening = null);

internal sealed record V4ProjectionHistory(ScenarioRunId ExecutionRunId, ObservedFinanceState Genesis,
    IReadOnlyList<V4ProjectionSegment> Segments);

internal sealed partial class V4ObservationProjector
{
    private sealed class BankActivity
    {
        internal long Intents, Submitted, Screened, Cleared, Held, Rejected, SettledIn, SettledOut, SettledTransfers,
            ReturnedIn, ReturnedOut, Returns, Reserved, Released;
        internal HashSet<string> Senders { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> Receivers { get; } = new(StringComparer.Ordinal);
    }

    private readonly SortedDictionary<string, BankActivity> banks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> pending = new(StringComparer.Ordinal);
    private readonly List<V4BankObservation> bankRows = [];
    private Dictionary<string, ObservationBalance> openingBalances = null!;

    internal static V4ObservationProjector Project(V4ProjectionHistory history)
    {
        var projector = new V4ObservationProjector();
        var state = history.Genesis;
        if (history.ExecutionRunId.IsEmpty || state.LastFact != 0 || history.Segments.Count == 0)
        {
            throw new InvalidOperationException("A projection must start at the pinned opening.");
        }

        projector.openingBalances = BankBalances(state);
        var catalog = Enumerable.Range(0, BehavioralEpisodeScenario.BankCount).Select(FridayRingScenario.BankId);
        if (!catalog.SequenceEqual(projector.openingBalances.Keys.Order(StringComparer.Ordinal)))
        {
            throw new InvalidOperationException("Observation genesis does not contain the six supported banks.");
        }

        foreach (var bank in projector.openingBalances.Keys)
        {
            projector.banks.Add(bank, new());
            projector.pending.Add(bank, 0);
        }

        projector.Begin(history, state);
        foreach (var segment in history.Segments)
        {
            if (segment.Opening is { } fork && (state.LastFact != fork.FactSequence || state.PrefixDigest != fork.PrefixDigest
                || state.LastSealedBoundaryMs != fork.BoundaryMs || state.LastEffectiveTimeMs != fork.BoundaryMs))
            {
                throw new InvalidOperationException("The projection does not match the inherited fork seal.");
            }
            foreach (var stored in segment.Facts)
            {
                var fact = stored.Input;
                var next = stored.Apply(state, segment.Context);
                if (fact.Kind == ObservedFactKind.ObservationSeal)
                {
                    var seal = new V4SealIdentity(history.ExecutionRunId, state.LastSealedBoundaryMs, next.LastSealedBoundaryMs,
                        next.LastFact, next.PrefixDigest);
                    projector.CloseBankInterval(seal, next);
                    projector.CloseAccountInterval(seal, next);
                }
                else
                {
                    var intentId = PaymentId(fact);
                    var payment = next.Ledger.Payments[intentId];
                    var prior = state.Ledger.Payments.GetValueOrDefault(intentId);
                    projector.ObserveAccounts(fact, state, payment, prior);
                    projector.ObserveBank(fact.Kind, payment, prior);
                }
                state = next;
            }
            if (state.LastFact != segment.LastFact)
            {
                throw new InvalidOperationException("Projection facts are incomplete at the requested position.");
            }
        }
        return projector;
    }

    internal IReadOnlyList<V4BankObservation> Banks => bankRows;

    partial void Begin(V4ProjectionHistory history, ObservedFinanceState opening);
    partial void ObserveAccounts(ObservedFinanceInput fact, ObservedFinanceState state, LedgerPayment payment, LedgerPayment? prior);
    partial void CloseAccountInterval(V4SealIdentity seal, ObservedFinanceState state);

    private void ObserveBank(ObservedFactKind kind, LedgerPayment payment, LedgerPayment? prior)
    {
        var intent = payment.Intent;
        var amount = intent.Amount.MinorUnits;
        var payer = banks[intent.SourceBankId];
        var payee = banks[intent.DestBankId];
        checked
        {
            if (kind == ObservedFactKind.PaymentIntent)
            {
                payer.Intents++;
                payer.Submitted += amount;
                payer.Senders.Add(intent.SourceAccountId);
                payer.Receivers.Add(intent.DestAccountId);
                pending[intent.SourceBankId]++;
            }
            if (kind == ObservedFactKind.Screening)
            {
                pending[intent.SourceBankId]--;
                payer.Screened++;
                switch (payment.Status)
                {
                    case PaymentStatus.Cleared: payer.Cleared++; break;
                    case PaymentStatus.Held: payer.Held++; break;
                    case PaymentStatus.Rejected: payer.Rejected++; break;
                    default: throw new InvalidOperationException("Unexpected screening outcome.");
                }
            }
            if (prior is not null)
            {
                var delta = payment.ReservedMinor - prior.ReservedMinor;
                if (delta > 0) payer.Reserved += delta;
                else payer.Released -= delta;
                if (!prior.SettlementPosted && payment.SettlementPosted)
                {
                    payer.SettledOut += amount;
                    payee.SettledIn += amount;
                    payer.SettledTransfers++;
                    if (payer != payee) payee.SettledTransfers++;
                }
                if (!prior.ReturnPosted && payment.ReturnPosted)
                {
                    payee.ReturnedOut += amount;
                    payer.ReturnedIn += amount;
                    payer.Returns++;
                    if (payer != payee) payee.Returns++;
                }
            }
        }
    }

    private void CloseBankInterval(V4SealIdentity seal, ObservedFinanceState state)
    {
        var closing = BankBalances(state);
        long systemSettled = 0, systemReturned = 0;
        foreach (var (id, a) in banks)
        {
            var balance = closing[id];
            var previous = openingBalances[id];
            var row = new V4BankObservation(seal, id, balance, a.Intents, a.Submitted, a.Senders.Count, a.Receivers.Count,
                a.Screened, a.Cleared, a.Held, a.Rejected, pending[id], a.SettledIn, a.SettledOut, a.SettledTransfers,
                a.ReturnedIn, a.ReturnedOut, a.Returns, a.Reserved, a.Released);
            var returnNet = checked(row.ReturnedIn - row.ReturnedOut);
            if (checked(previous.Posted + row.NetSettled + returnNet) != balance.Posted
                || checked(previous.Reserved + row.Reserved - row.Released) != balance.Reserved || row.ScreeningPending < 0)
            {
                throw new InvalidOperationException("Observation bank flows do not reconcile to verified balances.");
            }
            systemSettled = checked(systemSettled + row.NetSettled);
            systemReturned = checked(systemReturned + returnNet);
            bankRows.Add(row);
        }
        if (systemSettled != 0 || systemReturned != 0)
        {
            throw new InvalidOperationException("Internal transfers must conserve system principal.");
        }
        foreach (var bank in closing.Keys) banks[bank] = new();
        openingBalances = closing;
    }

    private static Dictionary<string, ObservationBalance> BankBalances(ObservedFinanceState state)
    {
        var totals = new Dictionary<string, ObservationBalance>(StringComparer.Ordinal);
        foreach (var a in state.Ledger.Accounts.Values)
        {
            if (a.ReservedMinor < 0 || a.PostedMinor < a.ReservedMinor)
            {
                throw new InvalidOperationException("Financial observation encountered an invalid account balance.");
            }
            var prior = totals.GetValueOrDefault(a.BankId);
            totals[a.BankId] = new(checked(prior.Posted + a.PostedMinor), checked(prior.Reserved + a.ReservedMinor));
        }
        return totals;
    }

    private static string PaymentId(ObservedFinanceInput fact) => fact.Kind switch
    {
        ObservedFactKind.PaymentIntent => PaymentIntentCodec.Instance.Decode(fact.Payload.AsSpan()).IntentId,
        ObservedFactKind.Screening or ObservedFactKind.Settlement => PaymentStepCodec.Decode(fact.Payload.AsSpan()),
        ObservedFactKind.ReviewDecision => ReviewDecisionCodec.Decode(fact.Payload.AsSpan()).IntentId,
        ObservedFactKind.ReturnInstruction => ReturnInstructionCodec.Decode(fact.Payload.AsSpan()).IntentId,
        _ => throw new InvalidOperationException("Unknown financial observation fact."),
    };
}
