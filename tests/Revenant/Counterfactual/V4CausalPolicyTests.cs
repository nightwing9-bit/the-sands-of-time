using Marrow.Timeline;
using Revenant.Counterfactual;
using Revenant.Cases;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Intelligence;
using Revenant.Observations;
using Revenant.Payments;
using Revenant.Scenario;
using Revenant.Timeline;
using Xunit;
using Xunit.Abstractions;

namespace Revenant.Tests.Counterfactual;

public sealed class V4CausalPolicyTests(ITestOutputHelper output)
{
    internal const long Origin = 1_789_203_000_000;
    internal const long Cutoff = Origin + V4ReviewCounts.WarmupMs;
    internal static readonly ScenarioRunId RunId = new(Guid.Parse("00000000-0000-0000-0000-000000000908"));

    [Fact]
    public void Stricter_base_policy_has_lawful_direct_and_indirect_effects() => Execute(0);

    [Fact]
    public void Replay_regenerates_the_suffix_and_rejects_invalid_source_or_fork() => Execute(1);

    [Fact]
    public void Economics_reconciles_distinct_equal_principals_pending_exposure_and_signed_costs() => Execute(2);

    [Fact]
    public void Returned_settlement_is_recovery_and_shared_intervention_has_zero_incremental_cost()
    {
        var run = BehavioralEpisodeScenario.Create(RunId, 42, Origin, 600_000, [2, 2, 2, 2, 2, 2]);
        var context = Context(run, PolicyCatalog.V7);
        var genesis = ObservedFinanceReducer.Initial(run, context).Value;
        var account = run.Manifest.Accounts;
        var payer = account[0];
        var payee = account[2];
        var facts = new List<V4StoredFact>();
        var state = genesis;
        var at = Origin + 1;
        void Add(ObservedFinanceInput fact)
        {
            state = ObservedFinanceReducer.Apply(state, fact, context).Value;
            facts.Add(new(fact, state.PrefixDigest));
        }
        void Fact(ObservedFactKind kind, string schema, byte[] bytes) =>
            Add(new(RunId, state.LastFact + 1, at, kind, schema, [.. bytes]));
        void Intake(string id, int seq, bool sanctions)
        {
            var p = new PaymentIntent(RunId, id, seq, at, payer.BankId, payer.AccountId, payee.BankId, payee.AccountId,
                Money.Usd(150_000), SyntheticTruth.Legitimate, new(false, sanctions));
            Fact(ObservedFactKind.PaymentIntent, ScenarioSchemas.InputSchemaVersion, PaymentIntentCodec.Instance.EncodeCanonical(p));
            Add(V4ModelScreeningEvidence.Create(state, context, id, at, [], null, null).ToInput());
        }
        void Settle(string id) => Fact(ObservedFactKind.Settlement, PaymentStepCodec.SettlementSchema, PaymentStepCodec.Encode(id));
        Intake("recovered", 1, false);
        Settle("recovered");
        at++;
        Fact(ObservedFactKind.ReturnInstruction, ReturnInstructionCodec.SchemaVersion, ReturnInstructionCodec.Encode(new("recovered", "authorized-return")));
        Intake("shared-hold", 2, true);
        at += 120_000;
        Fact(ObservedFactKind.ReviewDecision, ReviewDecisionCodec.SchemaVersion, ReviewDecisionCodec.Encode(
            new("shared-hold", ReviewDecisionAction.Approve, 1, 1, nameof(CaseStatus.Released), ReviewPolicy.Version)));
        Settle("shared-hold");
        var boundary = Origin + ObservedFinanceVersion.BucketMs;
        Add(ObservedFinanceInput.Seal(new(RunId, boundary, Origin, state.LastFact, state.PrefixDigest,
            V4SimulationScheduler.InitialCommandDigest(RunId))));
        var history = new V4ProjectionHistory(RunId, genesis, [new(context, facts, state.LastFact)]);
        var same = new V4ReplayPlan(context, genesis, state, state, [], [], history, history);
        var money = V4CounterfactualEconomics.Compare(same, new Dictionary<string, V4EvaluationTruth>
        { ["recovered"] = V4EvaluationTruth.Malicious, ["shared-hold"] = V4EvaluationTruth.Legitimate });
        Assert.Equal(2, money.Payments.Count);
        Assert.Equal(150_000L, money.Source.MaliciousSettledMinor);
        Assert.Equal(150_000L, money.Source.MaliciousRecoveredMinor);
        Assert.Equal(0L, money.Source.InterceptedMinor);
        Assert.Equal(1, money.Source.LegitimateReleasedCount);
        Assert.Equal(1, money.Alternative.LegitimateReleasedCount);
        Assert.Equal(250L, money.Source.InterventionCostMinor);
        Assert.Equal(250L, money.Alternative.InterventionCostMinor);
        Assert.Equal(0, money.AlternativeDelayedCount);
        Assert.Equal(0L, money.NetEffectMinor);
    }

    [Fact]
    public void Repeated_real_recoveries_that_exceed_int64_fail_closed()
    {
        var run = BehavioralEpisodeScenario.Create(RunId, 42, Origin, 1_200_000, [2, 2, 2, 2, 2, 2]);
        var context = Context(run, PolicyCatalog.V7);
        var opening = ObservedFinanceReducer.Initial(run, context).Value;
        var payer = run.Manifest.Accounts[0];
        var payee = run.Manifest.Accounts[2];
        const long principal = 3_100_000_000_000_000_000;
        var genesis = opening with { Ledger = opening.Ledger with
        {
            Accounts = opening.Ledger.Accounts.SetItem(payer.AccountId, opening.Ledger.Accounts[payer.AccountId] with { PostedMinor = principal })
        } };
        var state = genesis;
        var facts = new List<V4StoredFact>();
        var labels = new Dictionary<string, V4EvaluationTruth>();
        for (var i = 1; i <= 3; i++)
        {
            var at = state.LastSealedBoundaryMs + 1;
            var id = $"large-{i}";
            var p = new PaymentIntent(RunId, id, i, at, payer.BankId, payer.AccountId, payee.BankId, payee.AccountId,
                Money.Usd(principal), SyntheticTruth.Legitimate, new(false, false));
            Fact(at, ObservedFactKind.PaymentIntent, ScenarioSchemas.InputSchemaVersion, PaymentIntentCodec.Instance.EncodeCanonical(p));
            Add(V4ModelScreeningEvidence.Create(state, context, id, at, [], null, null).ToInput());
            Fact(at, ObservedFactKind.Settlement, PaymentStepCodec.SettlementSchema, PaymentStepCodec.Encode(id));
            Fact(at, ObservedFactKind.ReturnInstruction, ReturnInstructionCodec.SchemaVersion, ReturnInstructionCodec.Encode(new(id, "authorized-return")));
            var boundary = ObservedFinanceVersion.NextBoundary(state.LastSealedBoundaryMs);
            Add(ObservedFinanceInput.Seal(new(RunId, boundary, state.LastSealedBoundaryMs, state.LastFact, state.PrefixDigest,
                V4SimulationScheduler.InitialCommandDigest(RunId))));
            labels.Add(id, V4EvaluationTruth.Malicious);
        }
        var history = new V4ProjectionHistory(RunId, genesis, [new(context, facts, state.LastFact)]);
        var plan = new V4ReplayPlan(context, genesis, state, state, [], [], history, history);
        Assert.Throws<OverflowException>(() => V4CounterfactualEconomics.Compare(plan, labels));

        void Fact(long at, ObservedFactKind kind, string schema, byte[] bytes) =>
            Add(new(RunId, state.LastFact + 1, at, kind, schema, [.. bytes]));
        void Add(ObservedFinanceInput fact)
        {
            state = ObservedFinanceReducer.Apply(state, fact, context).Value;
            facts.Add(new(fact, state.PrefixDigest));
        }
    }

    private void Execute(int test)
    {
        var run = BehavioralEpisodeScenario.Create(RunId, 42, Origin, V4ReviewCounts.WarmupMs + 600_000, [2, 2, 2, 2, 2, 2]);
        var sourceContext = Context(run, PolicyCatalog.V7);
        var childContext = Context(run, PolicyCatalog.V8);
        V4BranchExecution.ValidateChildContext(sourceContext, childContext);
        var model = BehavioralRiskScorerV2.Load(Path.Combine(AppContext.BaseDirectory, "Intelligence", "Artifacts",
            BehavioralRiskScorerV2.ArtifactDirectory), sourceContext).Value;
        var genesis = ObservedFinanceReducer.Initial(run, sourceContext).Value;
        var source = new Fold(genesis, sourceContext, model);
        while (source.State.LastSealedBoundaryMs < Cutoff) source.Seal();
        var fork = source.State;
        var child = new Fold(genesis, childContext, model, new(sourceContext, source.Facts.ToArray(), fork.LastFact), fork);
        Assert.Equal(6, run.Manifest.Banks.Length);

        var accounts = run.Manifest.Accounts;
        var a = Intent("intercepted", 1, 0, 0, 2, 150_000, true, SyntheticTruth.FraudMuleRing);
        var b = Intent("delayed", 2, 0, 1, 4, 150_000, true, SyntheticTruth.Legitimate);
        var c = Intent("deferred", 3, 60_000, 3, 6, 150_000, true, SyntheticTruth.FraudMuleRing);
        var d = Intent("funding", 4, 60_000, 3, 8, accounts[3].OpeningBalance.MinorUnits, false, SyntheticTruth.Legitimate);
        var replenish = Intent("replenish", 5, 120_000, 10, 3, 150_000, false, SyntheticTruth.Legitimate);

        Both(a);
        source.Settle(a, Cutoff);
        Assert.Equal(PaymentStatus.Held, child.Payment(a).Status);
        Assert.Equal(CaseDecisionKind.MfaRequired, child.Review(a, Cutoff, CaseAction.Reject, new("analyst", true, false, false)).Kind);
        Assert.Equal(CaseDecisionKind.Accepted, child.Review(a, Cutoff, CaseAction.Reject, new("analyst", true, false, true)).Kind);
        Assert.Equal(PaymentStatus.Settled, source.Payment(a).Status);
        Assert.True(source.Payment(a).SettlementPosted);
        Assert.Equal(PaymentStatus.Rejected, child.Payment(a).Status);
        Assert.Equal(CaseStatus.Rejected, child.Payment(a).Case);
        Assert.False(child.Payment(a).SettlementPosted);

        Both(b);
        source.Settle(b, Cutoff);
        Assert.Equal(PaymentStatus.Held, child.Payment(b).Status);
        Both(c);
        Assert.Equal(150_000L, child.State.Ledger.Accounts[c.SourceAccountId].ReservedMinor);
        Both(d);
        source.Settle(d, Cutoff + 60_000);
        child.Settle(d, Cutoff + 60_000);
        Assert.Equal(PaymentStatus.Settled, source.Payment(d).Status);
        Assert.Equal(PaymentStatus.Returned, child.Payment(d).Status);
        Assert.Equal(PaymentReason.InsufficientAvailable, child.Payment(d).Reason);
        Assert.False(child.Payment(d).SettlementPosted);

        Both(replenish);
        source.Settle(replenish, Cutoff + 120_000);
        child.Settle(replenish, Cutoff + 120_000);
        source.Settle(c, Cutoff + 120_000);
        Assert.Equal(CaseDecisionKind.Accepted, child.Review(b, Cutoff + 120_000, CaseAction.Approve, new("analyst", true, false, true)).Kind);
        child.Settle(b, Cutoff + 120_000);
        source.Seal();
        child.Seal();

        Assert.Equal(PaymentStatus.Settled, child.Payment(b).Status);
        Assert.Equal(120_000L, child.Settlements[b.IntentId] - source.Settlements[b.IntentId]);
        Assert.Equal(PaymentStatus.Settled, source.Payment(c).Status);
        Assert.Equal(PaymentStatus.Held, child.Payment(c).Status);
        Assert.False(child.Payment(c).SettlementPosted);
        Assert.Equal(150_000L, child.Payment(c).ReservedMinor);
        Assert.Equal(new[] { CaseAction.Reject, CaseAction.Approve }, child.Reviews.Select(x => x.Action));
        Assert.All(child.Reviews, x => Assert.Equal("analyst", x.Actor.SubjectId));
        Assert.Empty(source.Reviews);
        Assert.Equal(source.State.Ledger.Payments.Keys, child.State.Ledger.Payments.Keys);
        foreach (var fold in new[] { source, child })
        {
            Assert.Equal(genesis.Ledger.Accounts.Values.Sum(x => x.PostedMinor), fold.State.Ledger.Accounts.Values.Sum(x => x.PostedMinor));
            Assert.Equal(fold.State.Ledger.Payments.Values.Sum(x => x.ReservedMinor), fold.State.Ledger.Accounts.Values.Sum(x => x.ReservedMinor));
            Assert.All(fold.State.Ledger.Accounts.Values, x => Assert.InRange(x.AvailableMinor, 0, x.PostedMinor));
            foreach (var p in fold.State.Ledger.Payments.Values)
                Assert.All(p.Trail.Zip(p.Trail.Skip(1)), edge => Assert.True(PaymentLifecycle.IsLegal(edge.First, edge.Second)));
        }
        Assert.NotEqual(source.Evidence[d.IntentId].VectorDigest, child.Evidence[d.IntentId].VectorDigest);
        Assert.Equal(model.Pin, child.Evidence[d.IntentId].Model);
        Assert.Equal(PolicyCatalog.RefOf(PolicyCatalog.V8), child.Evidence[d.IntentId].BasePolicy);
        output.WriteLine($"policy-v7: {sourceContext.BasePolicy.ConfigHash}; policy-v8: {childContext.BasePolicy.ConfigHash}");
        output.WriteLine($"Source/child Screening PPM for funding: {source.Evidence[d.IntentId].Ppm}/{child.Evidence[d.IntentId].Ppm}; funding principal: {d.Amount.MinorUnits}");
        output.WriteLine($"Source prefix: {source.State.PrefixDigest}; child prefix: {child.State.PrefixDigest}");

        if (test > 0)
        {
            TimelineId sourceId = new(RunId.Value);
            ScenarioRunId childId = new(Guid.Parse("00000000-0000-0000-0000-000000000909"));
            var digest = V4Counterfactual.StateDigest(source.State);
            var recordHash = source.State.PrefixDigest; // Verified Marrow receipt is supplied at the pure port; SQL proof is Gate 4.
            var contextHash = V4Counterfactual.ContextDigest(sourceContext);
            var key = ObservedFinanceTimelineVersions.V4;
            var manifest = new HistoryManifest(sourceId, key.DomainId, "analyst", key.InputSchemaVersion, key.StateSchemaVersion,
                key.ContextSchemaVersion, contextHash, "sha256:" + contextHash, key.ReducerVersion, V4BranchExecution.FamilyRulesHash,
                run.Manifest.ContentHash, source.State.LastFact, digest, recordHash, null, null, HistoryStatus.Verified);
            var proof = new VerifiedReplay<ObservedFinanceState>(sourceId, source.State.LastFact, source.State, digest, recordHash,
                HistoryStatus.Verified);
            var input = new V4ReplaySource(manifest, proof, genesis, sourceContext, source.Facts.ToArray(), []);
            V4ReplayAction[] interventions =
            [
                new("intercept", a.IntentId, Cutoff, 1, new("analyst", true, false, true), CaseAction.Reject, null),
                new("release", b.IntentId, Cutoff + 120_000, 1, new("analyst", true, false, true), CaseAction.Approve, null),
            ];
            var horizon = Cutoff + ObservedFinanceVersion.BucketMs;
            var originalBytes = ObservedFinanceStateCodec.Instance.EncodeCanonical(source.State);
            var originalFacts = input.Facts.Select(x => ObservedFinanceInputCodec.Instance.EncodeCanonical(x.Input)).ToArray();
            var plan = V4Counterfactual.Plan(input, fork.LastFact, horizon, childId, childContext,
                V4ComparisonMode.PolicyAndSandboxIntervention, interventions, model);
            var retry = V4Counterfactual.Plan(input, fork.LastFact, horizon, childId, childContext,
                V4ComparisonMode.PolicyAndSandboxIntervention, interventions, model);
            Assert.Equal(originalBytes, ObservedFinanceStateCodec.Instance.EncodeCanonical(plan.SourceFinal));
            Assert.Equal(ObservedFinanceStateCodec.Instance.EncodeCanonical(fork), ObservedFinanceStateCodec.Instance.EncodeCanonical(plan.Fork));
            Assert.Equal(plan.AlternativeFinal.PrefixDigest, retry.AlternativeFinal.PrefixDigest);
            Assert.Equal(plan.Actions, retry.Actions);
            Assert.All(plan.Actions, x => Assert.Equal(V4ActionDisposition.Applied, x.Disposition));
            Assert.Equal(PaymentStatus.Rejected, plan.AlternativeFinal.Ledger.Payments[a.IntentId].Status);
            Assert.Equal(PaymentStatus.Settled, plan.AlternativeFinal.Ledger.Payments[b.IntentId].Status);
            Assert.Equal(PaymentStatus.Held, plan.AlternativeFinal.Ledger.Payments[c.IntentId].Status);
            Assert.Equal(PaymentReason.InsufficientAvailable, plan.AlternativeFinal.Ledger.Payments[d.IntentId].Reason);
            Assert.NotEqual(plan.SourceFinal.PrefixDigest, plan.AlternativeFinal.PrefixDigest);
            Assert.Equal(originalBytes, ObservedFinanceStateCodec.Instance.EncodeCanonical(source.State));
            Assert.All(input.Facts.Select((x, i) => (x, i)), pair =>
                Assert.Equal(originalFacts[pair.i], ObservedFinanceInputCodec.Instance.EncodeCanonical(pair.x.Input)));
            Assert.NotEmpty(V4ObservationProjector.Project(plan.AlternativeHistory, captureRisk: true).Banks);
            var same = V4Counterfactual.Plan(input, fork.LastFact, horizon, childId, sourceContext, V4ComparisonMode.PolicyOnly, [], model);
            Assert.Equal(PaymentStatus.Settled, same.AlternativeFinal.Ledger.Payments[a.IntentId].Status);
            Assert.Throws<InvalidOperationException>(() => V4Counterfactual.Plan(input with { Manifest = manifest with { SeedHash = "corrupt" } },
                fork.LastFact, horizon, childId, childContext, V4ComparisonMode.PolicyOnly, [], model));
            Assert.Throws<InvalidOperationException>(() => V4Counterfactual.Plan(input, fork.LastFact + 1, horizon, childId,
                childContext, V4ComparisonMode.PolicyOnly, [], model));
            var stale = V4Counterfactual.Plan(input, fork.LastFact, horizon, childId, childContext,
                V4ComparisonMode.PolicyAndSandboxIntervention, [interventions[0] with { ExpectedVersion = 2 }], model);
            Assert.Equal(V4ActionDisposition.InapplicableUnderBranch, Assert.Single(stale.Actions).Disposition);
            Assert.Equal(nameof(CaseDecisionKind.Stale), Assert.Single(stale.Actions).Code);
            foreach (var invalid in new[]
            {
                interventions[0] with { Actor = new("", true, false, true) },
                interventions[0] with { Actor = new("analyst", false, false, true) },
                interventions[0] with { Actor = new("analyst", true, false, false) },
                interventions[0] with { ExpectedVersion = 0 },
            })
                Assert.Contains("invalid intervention provenance", Assert.Throws<InvalidOperationException>(() =>
                    V4Counterfactual.Plan(input, fork.LastFact, horizon, childId, childContext,
                        V4ComparisonMode.PolicyAndSandboxIntervention, [invalid], model)).Message);

            if (test == 2)
            {
                var labels = new Dictionary<string, V4EvaluationTruth>
                {
                    [a.IntentId] = V4EvaluationTruth.Malicious, [b.IntentId] = V4EvaluationTruth.Legitimate,
                    [c.IntentId] = V4EvaluationTruth.Malicious, [d.IntentId] = V4EvaluationTruth.Legitimate,
                };
                var money = V4CounterfactualEconomics.Compare(plan, labels);
                Assert.Equal(5, money.Payments.Count);
                Assert.Equal(1, money.UnknownTruthCount);
                Assert.Equal(0, money.Source.InterceptedCount);
                Assert.Equal(1, money.Alternative.InterceptedCount);
                Assert.Equal(150_000, money.InterceptedDeltaMinor);
                Assert.Equal(1, money.Alternative.DeferredCount);
                Assert.Equal(150_000, money.Alternative.DeferredMinor);
                Assert.Equal(300_000, money.Source.MaliciousSettledMinor);
                Assert.Equal(0, money.Alternative.MaliciousSettledMinor);
                Assert.Equal(150_000, money.AvoidedExposureMinor);
                Assert.Equal(2, money.AlternativeDelayedCount);
                Assert.Equal(6, money.AlternativeAdditionalDelayMinutes);
                Assert.Equal(250, money.InterventionCostDeltaMinor);
                Assert.Equal(60, money.DelayCostDeltaMinor);
                Assert.Equal(149_690, money.NetEffectMinor);
                Assert.Equal(1496.90m, money.NetEffectMinor / 100m);
                Assert.Equal(V4ImpactCause.IndirectFunding, money.Payments.Single(x => x.IntentId == d.IntentId).Alternative.Cause);
                Assert.All(money.Balances, x =>
                {
                    Assert.Equal(x.Source.Posted - x.Source.Reserved, x.Source.Available);
                    Assert.Equal(x.Alternative.Posted - x.Alternative.Reserved, x.Alternative.Available);
                });
                Assert.Equal(money.InterventionCostDeltaMinor, money.Payments.Sum(x => x.InterventionCostDeltaMinor));
                Assert.Equal(money.DelayCostDeltaMinor, money.Payments.Sum(x => x.DelayCostDeltaMinor));
                Assert.Equal(money.AvoidedExposureMinor, money.Payments.Sum(x => x.AvoidedExposureDeltaMinor));
                Assert.Equal(money.EvidenceDigest, V4CounterfactualEconomics.Compare(retry, labels).EvidenceDigest);
                var noChange = V4CounterfactualEconomics.Compare(same, labels);
                Assert.Equal(0, noChange.NetEffectMinor);
                var costOnly = V4CounterfactualEconomics.Compare(stale, labels);
                Assert.True(costOnly.NetEffectMinor < 0);
                var wrong = V4Counterfactual.Plan(input, fork.LastFact, horizon, childId, childContext,
                    V4ComparisonMode.PolicyAndSandboxIntervention, [interventions[1] with { Review = CaseAction.Reject }], model);
                var wrongful = V4CounterfactualEconomics.Compare(wrong, labels);
                Assert.Equal(1, wrongful.Alternative.LegitimateRejectedCount);
                Assert.Equal(250, wrongful.InterventionCostDeltaMinor);
                output.WriteLine($"Economics: {money.InterceptedDeltaMinor} intercepted, {money.AvoidedExposureMinor} avoided, "
                    + $"{money.AlternativeDelayedCount} delayed, {money.InterventionCostDeltaMinor} intervention, "
                    + $"{money.DelayCostDeltaMinor} delay, {money.NetEffectMinor} net; evidence {money.EvidenceDigest}");
            }
        }

        PaymentIntent Intent(string id, int sequence, long offset, int payer, int payee, long amount, bool isNew, SyntheticTruth truth) =>
            new(RunId, id, sequence, Cutoff + offset, accounts[payer].BankId, accounts[payer].AccountId,
                accounts[payee].BankId, accounts[payee].AccountId, Money.Usd(amount), truth, new(isNew, false));

        void Both(PaymentIntent p)
        {
            source.IntakeAndScreen(p);
            child.IntakeAndScreen(p);
        }
    }

    internal static ObservedFinanceContext Context(ScenarioRun run, PolicyConfig policy) => new(run.Manifest.ScenarioVersion,
        run.Manifest.ContentHash, run.Manifest.StartUtcMs, PolicyCatalog.RefOf(policy), ReviewPolicy.Version,
        ObservedRiskMode.ModelRequired, BehavioralRiskScorerV2.FeatureSchema, BehavioralRiskScorerV2.PublishedPin, [], []);

    private sealed class Fold(ObservedFinanceState genesis, ObservedFinanceContext context, BehavioralRiskScorerV2 model,
        V4ProjectionSegment? prefix = null, ObservedFinanceState? inherited = null)
    {
        internal ObservedFinanceState State { get; private set; } = inherited ?? genesis;
        internal List<V4StoredFact> Facts { get; } = [];
        internal Dictionary<string, V4ModelScreeningEvidence> Evidence { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, long> Settlements { get; } = new(StringComparer.Ordinal);
        internal List<(CaseAction Action, CaseActor Actor, long ExpectedVersion)> Reviews { get; } = [];
        internal LedgerPayment Payment(PaymentIntent p) => State.Ledger.Payments[p.IntentId];

        internal void IntakeAndScreen(PaymentIntent p)
        {
            Apply(Fact(p.EventTimeUtcMs, ObservedFactKind.PaymentIntent, ScenarioSchemas.InputSchemaVersion,
                PaymentIntentCodec.Instance.EncodeCanonical(p)));
            var segment = new V4ProjectionSegment(context, Facts, State.LastFact);
            var vector = V4ObservationProjector.RiskAt(new(RunId, genesis, prefix is null ? [segment] : [prefix, segment]),
                p.IntentId, p.EventTimeUtcMs).Vector;
            var result = model.Score(vector).Value;
            var evidence = V4ModelScreeningEvidence.Create(State, context, p.IntentId, p.EventTimeUtcMs,
                [.. vector.Encode()], BitConverter.SingleToInt32Bits(result.RawScore), result.RiskPpm);
            Apply(evidence.ToInput());
            Evidence.Add(p.IntentId, evidence);
        }

        internal void Seal()
        {
            var next = ObservedFinanceVersion.NextBoundary(State.LastSealedBoundaryMs);
            Apply(ObservedFinanceInput.Seal(new(RunId, next, State.LastSealedBoundaryMs, State.LastFact,
                State.PrefixDigest, V4SimulationScheduler.InitialCommandDigest(RunId))));
        }

        internal void Settle(PaymentIntent p, long at)
        {
            Apply(Fact(at, ObservedFactKind.Settlement, PaymentStepCodec.SettlementSchema, PaymentStepCodec.Encode(p.IntentId)));
            if (Payment(p).SettlementPosted) Settlements.Add(p.IntentId, at);
        }

        internal CaseDecision Review(PaymentIntent p, long at, CaseAction action, CaseActor actor)
        {
            var payment = Payment(p);
            // Each target is a newly opened case, with no prior assignments or approvals.
            Assert.Equal(CaseStatus.Open, payment.Case);
            Assert.Equal(0, payment.Approvals);
            const long expectedVersion = 1;
            var result = CaseMachine.Decide(new(payment.Case!.Value, expectedVersion, payment.RequiredApprovals, []), action, actor, expectedVersion);
            if (result.Kind == CaseDecisionKind.Accepted)
            {
                Apply(Fact(at, ObservedFactKind.ReviewDecision, ReviewDecisionCodec.SchemaVersion,
                    ReviewDecisionCodec.Encode(new(p.IntentId, action == CaseAction.Reject ? ReviewDecisionAction.Reject : ReviewDecisionAction.Approve,
                        result.ApprovalOrder, payment.RequiredApprovals, result.NextStatus.ToString(), ReviewPolicy.Version))));
                Reviews.Add((action, actor, expectedVersion));
            }
            return result;
        }

        private ObservedFinanceInput Fact(long at, ObservedFactKind kind, string schema, byte[] bytes) =>
            new(RunId, State.LastFact + 1, at, kind, schema, [.. bytes]);

        private void Apply(ObservedFinanceInput fact)
        {
            var result = ObservedFinanceReducer.Apply(State, fact, context);
            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.ToString() : null);
            State = result.Value;
            Facts.Add(new(fact, State.PrefixDigest));
        }
    }
}
