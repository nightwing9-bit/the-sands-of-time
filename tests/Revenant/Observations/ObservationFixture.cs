using Revenant.Contracts;
using Revenant.Observations;
using Revenant.Payments;
using Revenant.Scenario;
using Revenant.Tests.Scenario;
using Revenant.Timeline;

namespace Revenant.Tests.Observations;

internal sealed class ObservationFixture
{
    private readonly List<V4StoredFact> facts = [];
    internal ScenarioRun Run { get; }
    internal ObservedFinanceContext Context { get; }
    internal ObservedFinanceState Initial { get; }
    internal ObservedFinanceState State { get; private set; }
    internal long Origin => Context.OriginMs;
    internal IReadOnlyList<V4StoredFact> Facts => facts;

    internal ObservationFixture(ScenarioRun? run = null)
    {
        Run = run ?? BehavioralEpisodeScenarioTests.Opening(BehavioralEpisodeScenarioTests.RunId);
        Context = BehavioralEpisodeScenarioTests.Pin(Run);
        State = Initial = ObservedFinanceReducer.Initial(Run, Context).Value;
    }

    internal PaymentIntent Intent(string id, long time, long amount = 5_000, int payer = 0, int payee = 2, bool sanctioned = false)
    {
        var source = Run.Manifest.Accounts[payer];
        var dest = Run.Manifest.Accounts[payee];
        var p = new PaymentIntent(Run.RunId, id, State.Ledger.LastIntentSequence + 1, time, source.BankId, source.AccountId,
            dest.BankId, dest.AccountId, Money.Usd(amount), SyntheticTruth.Legitimate, new(true, sanctioned));
        Intake(p);
        return p;
    }

    internal void Intake(PaymentIntent intent) => Add(ObservedFactKind.PaymentIntent, intent.EventTimeUtcMs,
        ScenarioSchemas.InputSchemaVersion, PaymentIntentCodec.Instance.EncodeCanonical(intent));

    internal void Screen(PaymentIntent intent, long time) =>
        Add(ObservedFactKind.Screening, time, PaymentStepCodec.ScreeningSchema, PaymentStepCodec.Encode(intent.IntentId));

    internal void Settle(PaymentIntent intent, long time) =>
        Add(ObservedFactKind.Settlement, time, PaymentStepCodec.SettlementSchema, PaymentStepCodec.Encode(intent.IntentId));

    internal void Return(PaymentIntent intent, long time) =>
        Add(ObservedFactKind.ReturnInstruction, time, ReturnInstructionCodec.SchemaVersion,
            ReturnInstructionCodec.Encode(new(intent.IntentId, "authorized-return")));

    internal void Seal()
    {
        var seal = ObservedFinanceInput.Seal(new(Run.RunId, ObservedFinanceVersion.NextBoundary(State.LastSealedBoundaryMs),
            State.LastSealedBoundaryMs, State.LastFact, State.PrefixDigest, new string('0', 64)));
        Append(seal);
    }

    internal V4ProjectionHistory History() => new(Run.RunId, Initial, [new(Context, facts.ToArray(), State.LastFact)]);

    private void Add(ObservedFactKind kind, long time, string schema, byte[] bytes) =>
        Append(new(Run.RunId, State.LastFact + 1, time, kind, schema, [.. bytes]));

    private void Append(ObservedFinanceInput fact)
    {
        State = ObservedFinanceReducer.Apply(State, fact, Context).Value;
        facts.Add(new(fact, State.PrefixDigest));
    }
}
