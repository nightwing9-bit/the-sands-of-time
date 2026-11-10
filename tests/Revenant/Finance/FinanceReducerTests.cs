using FsCheck;
using FsCheck.Fluent;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Tests.Scenario;
using Xunit;

namespace Revenant.Tests.Finance;

/// <summary>Task 5.2. **Validates: Requirements R05.02, R05.03, R05.08, R05.09, R05.11**</summary>
public sealed class FinanceReducerTests
{
    private static readonly ScenarioRunId RunA = new(Guid.Parse("00000000-0000-0000-0000-0000000005a2"));
    private static readonly ScenarioRunId RunB = new(Guid.Parse("00000000-0000-0000-0000-0000000005b2"));

    [Fact]
    public void Friday_ring_v7_settles_everything_and_v8_holds_the_ring_bursts_and_two_legit()
    {
        var run = FridayRingScenarioTests.Create(42, RunA);
        var v7 = Project(run, PolicyCatalog.V7);
        var v8 = Project(run, PolicyCatalog.V8);

        Assert.All(v7.Payments.Values, p => Assert.Equal(PaymentStatus.Settled, p.Status));
        Assert.Equal(new SyntheticLabelCounters(ScenarioSchemas.LabelSchemaVersion, 30, 0, 0, 60, 0, 0), v7.Counters);
        Assert.Equal(new SyntheticLabelCounters(ScenarioSchemas.LabelSchemaVersion, 20, 10, 0, 58, 2, 0), v8.Counters);

        // The held ring intents are exactly the 5th and 6th transfer of each hop, held by the velocity rule.
        var ringByPayer = v8.Payments.Values.Where(p => p.Intent.Truth == SyntheticTruth.FraudMuleRing)
            .GroupBy(p => p.Intent.SourceAccountId).ToList();
        Assert.Equal(5, ringByPayer.Count);
        foreach (var hop in ringByPayer)
        {
            var ordered = hop.OrderBy(p => p.Intent.Sequence).ToList();
            Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, ordered.Select(p => p.Velocity15m));
            Assert.All(ordered.Take(4), p => Assert.Equal(PaymentStatus.Settled, p.Status));
            Assert.All(ordered.Skip(4), p => Assert.Equal((PaymentStatus.Held, PaymentReason.VelocityRule), (p.Status, p.Reason)));
        }
        Assert.All(
            v8.Payments.Values.Where(p => p.Intent.Truth == SyntheticTruth.Legitimate && p.IsPendingHold),
            p => Assert.Equal(PaymentReason.NewBeneficiaryAmountRule, p.Reason));

        // Actual financial differences: same conserved total, different per-bank posted and reserved balances.
        Assert.Equal(v7.TotalPostedMinor, v8.TotalPostedMinor);
        Assert.Equal(0, v7.TotalReservedMinor);
        Assert.Equal(v8.Payments.Values.Where(p => p.IsPendingHold).Sum(p => p.Intent.Amount.MinorUnits), v8.TotalReservedMinor);
        Assert.NotEqual(v7.Banks.Select(b => b.PostedMinor), v8.Banks.Select(b => b.PostedMinor));
        Assert.NotEqual(v7.Accounts["BANK-A/ACC-3"].PostedMinor, v8.Accounts["BANK-A/ACC-3"].PostedMinor);
        Assert.True(v8.Accounts["BANK-A/ACC-3"].ReservedMinor > 0);
        AssertCausallyConsistent(run, v7);
        AssertCausallyConsistent(run, v8);
    }

    [Fact]
    public void Comparison_counts_pending_ring_holds_as_deferred_not_prevented()
    {
        var run = FridayRingScenarioTests.Create(42, RunA);
        var v7 = Project(run, PolicyCatalog.V7);
        var v8 = Project(run, PolicyCatalog.V8);

        var metrics = CounterfactualMetrics.Compare(v7, v8, CounterfactualCostModel.V1).Value;

        var heldLegit = v8.Payments.Values.Where(p => p.Intent.Truth == SyntheticTruth.Legitimate && p.IsPendingHold).ToList();
        var expectedDelayCost = heldLegit.Sum(p =>
            Math.Min((v8.ClockUtcMs - v7.Payments[p.Intent.IntentId].SettledAtUtcMs!.Value) / 60_000, 120) * 1_000);
        Assert.Equal(0, metrics.FraudPreventedCount);
        Assert.Equal(0, metrics.FraudPreventedExposureMinor);
        Assert.Equal(10, metrics.FraudDeferredAtHorizonCount);
        Assert.Equal(2, metrics.LegitimateDelayedCount);
        Assert.Equal(2 * 25_000, metrics.FalsePositiveCostMinor);
        Assert.Equal(expectedDelayCost, metrics.IncrementalDelayCostMinor);
        Assert.True(expectedDelayCost > 0);
        Assert.Equal(-(50_000 + expectedDelayCost), metrics.NetEffectMinor);
        Assert.Equal(12, metrics.DecisionDifferenceCount);
        Assert.Equal(ScenarioSchemas.LabelSchemaVersion, metrics.LabelSchemaVersion);

        var shorter = Project(run with { Intents = [.. run.Intents.Take(run.Intents.Length - 1)] }, PolicyCatalog.V8);
        Assert.Equal(FinanceErrorCode.NotAligned, CounterfactualMetrics.Compare(v7, shorter, CounterfactualCostModel.V1).Error.Code);
    }

    [Fact]
    public void Upstream_hold_starves_a_downstream_payment_which_v8_terminally_returns()
    {
        var run = FridayRingScenarioTests.Create(42, RunA);
        var t = run.Manifest.StartUtcMs;
        PaymentIntent[] intents =
        [
            Intent(run, 1, t, "BANK-A/ACC-3", "BANK-B/ACC-3", 900_000, SyntheticTruth.FraudMuleRing, newBeneficiary: true),
            Intent(run, 2, t + 1_000, "BANK-B/ACC-3", "BANK-C/ACC-3", 1_500_000, SyntheticTruth.FraudMuleRing),
        ];
        var v7 = Project(run with { Intents = [.. intents] }, PolicyCatalog.V7);
        var v8 = Project(run with { Intents = [.. intents] }, PolicyCatalog.V8);

        Assert.All(v7.Payments.Values, p => Assert.Equal(PaymentStatus.Settled, p.Status));
        Assert.Equal((PaymentStatus.Held, PaymentReason.NewBeneficiaryAmountRule), (v8.Payments["T-1"].Status, v8.Payments["T-1"].Reason));
        Assert.Equal((PaymentStatus.Returned, PaymentReason.InsufficientAvailable), (v8.Payments["T-2"].Status, v8.Payments["T-2"].Reason));
        Assert.Equal(
            new[] { PaymentStatus.Initiated, PaymentStatus.Screened, PaymentStatus.Cleared, PaymentStatus.Returned },
            v8.Payments["T-2"].Trail);

        var metrics = CounterfactualMetrics.Compare(v7, v8, CounterfactualCostModel.V1).Value;
        Assert.Equal(1, metrics.FraudPreventedCount);
        Assert.Equal(1_500_000, metrics.FraudPreventedExposureMinor);
        Assert.Equal(1, metrics.FraudDeferredAtHorizonCount);
        Assert.Equal(1_500_000, metrics.NetEffectMinor);
    }

    [Fact]
    public void Holds_reserve_availability_and_rejected_or_returned_paths_carry_reasons()
    {
        var run = FridayRingScenarioTests.Create(42, RunA);
        var t = run.Manifest.StartUtcMs;
        PaymentIntent[] intents =
        [
            Intent(run, 1, t, "BANK-A/ACC-1", "BANK-B/ACC-1", 4_000_000, SyntheticTruth.Legitimate, sanctions: true),
            Intent(run, 2, t + 1, "BANK-A/ACC-1", "BANK-B/ACC-1", 2_000_000, SyntheticTruth.Legitimate),
            Intent(run, 3, t + 2, "BANK-A/ACC-1", "BANK-C/ACC-1", 1_500_000, SyntheticTruth.Legitimate, sanctions: true),
            Intent(run, 4, t + 3, "BANK-A/ACC-2", "BANK-C/ACC-1", 1_000, SyntheticTruth.Legitimate, currency: "EUR"),
            Intent(run, 5, t + 4, "BANK-A/ACC-2", "BANK-C/ACC-1", 1_000_000, SyntheticTruth.Legitimate),
        ];
        var s = Project(run with { Intents = [.. intents] }, PolicyCatalog.V7);

        Assert.Equal((PaymentStatus.Held, PaymentReason.SanctionsHit), (s.Payments["T-1"].Status, s.Payments["T-1"].Reason));
        Assert.Equal((PaymentStatus.Returned, PaymentReason.InsufficientAvailable), (s.Payments["T-2"].Status, s.Payments["T-2"].Reason));
        Assert.Equal((PaymentStatus.Rejected, PaymentReason.InsufficientAvailable), (s.Payments["T-3"].Status, s.Payments["T-3"].Reason));
        Assert.Equal((PaymentStatus.Rejected, PaymentReason.UnsupportedCurrency), (s.Payments["T-4"].Status, s.Payments["T-4"].Reason));
        Assert.Equal(PaymentStatus.Settled, s.Payments["T-5"].Status);

        // The hold reserved availability without moving posted balance.
        Assert.Equal(new AccountPosition("BANK-A/ACC-1", "BANK-A", RunA.Scope("BANK-A/ACC-1"), 5_000_000, 4_000_000), s.Accounts["BANK-A/ACC-1"]);
        Assert.Equal(4_000_000, s.Accounts["BANK-A/ACC-2"].PostedMinor);
        Assert.Equal(6_000_000, s.Accounts["BANK-C/ACC-1"].PostedMinor);
        Assert.All(s.Payments.Values, p => Assert.True(PaymentLifecycle.IsLegalTrail(p.Trail)));
        AssertCausallyConsistent(run, s);
    }

    [Fact]
    public void Invalid_inputs_fail_typed_and_exact_retry_is_a_no_op()
    {
        var run = FridayRingScenarioTests.Create(42, RunA);
        var context = Pin(run, PolicyCatalog.V7);
        var s0 = FinanceReducer.Initial(run, context).Value;
        var first = run.Intents[0];
        var s1 = FinanceReducer.Apply(s0, first, context).Value;

        Assert.Same(s1, FinanceReducer.Apply(s1, first, context).Value);
        Assert.Equal(FinanceErrorCode.ConflictingDuplicate, FinanceReducer.Apply(s1, Retime(first, first.EventTimeUtcMs + 1), context).Error.Code);
        var skipped = FinanceReducer.Apply(s1, run.Intents[2], context).Value;
        Assert.Equal(FinanceErrorCode.OutOfOrderInput, FinanceReducer.Apply(skipped, run.Intents[1], context).Error.Code);
        Assert.Equal(FinanceErrorCode.OutOfOrderInput,
            FinanceReducer.Apply(s1, Retime(run.Intents[1], first.EventTimeUtcMs - 1), context).Error.Code);
        Assert.Equal(FinanceErrorCode.RunMismatch,
            FinanceReducer.Apply(s1, FridayRingScenarioTests.Create(42, RunB).Intents[1], context).Error.Code);
        Assert.Equal(FinanceErrorCode.UnknownAccount, FinanceReducer.Apply(s1,
            Intent(run, 9, first.EventTimeUtcMs, "BANK-A/ACC-9", "BANK-B/ACC-1", 1_000, SyntheticTruth.Legitimate), context).Error.Code);

        var otherSeed = Pin(FridayRingScenarioTests.Create(43, RunA), PolicyCatalog.V7);
        Assert.Equal(FinanceErrorCode.VersionMismatch, FinanceReducer.Apply(s1, run.Intents[1], otherSeed).Error.Code);
        Assert.Equal(FinanceErrorCode.VersionMismatch, FinanceReducer.Initial(run, otherSeed).Error.Code);
    }

    [Fact]
    public void A_v7_prefix_continues_under_v8_and_keeps_its_v7_decision_evidence()
    {
        var run = FridayRingScenarioTests.Create(42, RunA);
        var fork = run.Intents.First(i => i.Truth == SyntheticTruth.FraudMuleRing).Sequence - 1;
        var v7Context = Pin(run, PolicyCatalog.V7);
        var prefix = FinanceReducer.ApplyAll(FinanceReducer.Initial(run, v7Context).Value, run.Intents.Where(i => i.Sequence <= fork), v7Context).Value;

        var branch = FinanceReducer.ApplyAll(prefix, run.Intents.Where(i => i.Sequence > fork), Pin(run, PolicyCatalog.V8)).Value;

        Assert.All(branch.Payments.Values.Where(p => p.Intent.Sequence <= fork), p => Assert.Equal(PolicyCatalog.V7Id, p.PolicyId));
        Assert.All(branch.Payments.Values.Where(p => p.Intent.Sequence > fork), p => Assert.Equal(PolicyCatalog.V8Id, p.PolicyId));
        Assert.Equal(10, branch.Counters.FraudPendingHold);
        Assert.Equal(fork, prefix.LastSequence);
        Assert.All(prefix.Payments.Values, p => Assert.Equal(PaymentStatus.Settled, p.Status));
        AssertCausallyConsistent(run, branch);
    }

    [Fact]
    public void Property_any_seed_conserves_balances_keeps_legal_statuses_and_classifies_the_ring()
    {
        Prop.ForAll(Gen.Choose(int.MinValue, int.MaxValue).ToArbitrary(), seed =>
        {
            var run = FridayRingScenarioTests.Create(seed, RunA);
            var opening = run.InitialState.Accounts.Sum(a => a.OpeningBalance.MinorUnits);
            var finals = new List<FinanceProjectionState>();
            foreach (var policy in new[] { PolicyCatalog.V7, PolicyCatalog.V8 })
            {
                var context = Pin(run, policy);
                var state = FinanceReducer.Initial(run, context).Value;
                foreach (var intent in run.Intents)
                {
                    state = FinanceReducer.Apply(state, intent, context).Value;
                    if (state.TotalPostedMinor != opening
                        || state.Accounts.Values.Any(a => a.ReservedMinor < 0 || a.AvailableMinor < 0)
                        || state.Payments.Values.Any(p => !PaymentLifecycle.IsLegalTrail(p.Trail) || p.Trail[^1] != p.Status))
                    {
                        return false;
                    }
                }
                AssertCausallyConsistent(run, state);
                finals.Add(state);
            }
            var metrics = CounterfactualMetrics.Compare(finals[0], finals[1], CounterfactualCostModel.V1).Value;
            return finals[0].Counters.FraudPendingHold == 0 && finals[0].Counters.LegitimatePendingHold == 0
                && finals[1].Counters.FraudPendingHold == 10 && finals[1].Counters.LegitimatePendingHold == 2
                && metrics.FraudPreventedCount == 0 && metrics.FraudDeferredAtHorizonCount == 10
                && metrics.LegitimateDelayedCount == 2;
        }).Check(Config.QuickThrowOnFailure.WithMaxTest(50));
    }

    /// <summary>
    /// Independent check that balances are caused by decisions: posted equals opening plus settled in minus
    /// settled out, and reserved equals the account's pending holds.
    /// </summary>
    private static void AssertCausallyConsistent(ScenarioRun run, FinanceProjectionState state)
    {
        foreach (var opening in run.InitialState.Accounts)
        {
            var settled = state.Payments.Values.Where(p => p.Status == PaymentStatus.Settled).ToList();
            var expectedPosted = opening.OpeningBalance.MinorUnits
                + settled.Where(p => p.Intent.DestAccountId == opening.AccountId).Sum(p => p.Intent.Amount.MinorUnits)
                - settled.Where(p => p.Intent.SourceAccountId == opening.AccountId).Sum(p => p.Intent.Amount.MinorUnits);
            var expectedReserved = state.Payments.Values
                .Where(p => p.IsPendingHold && p.Intent.SourceAccountId == opening.AccountId)
                .Sum(p => p.Intent.Amount.MinorUnits);
            Assert.Equal(expectedPosted, state.Accounts[opening.AccountId].PostedMinor);
            Assert.Equal(expectedReserved, state.Accounts[opening.AccountId].ReservedMinor);
        }
        Assert.Equal(run.InitialState.Accounts.Sum(a => a.OpeningBalance.MinorUnits), state.Banks.Sum(b => b.PostedMinor));
        Assert.Equal(6, state.Banks.Length);
    }

    private static FinanceContext Pin(ScenarioRun run, PolicyConfig policy) =>
        FinanceContext.Pin(run.Manifest, PolicyCatalog.RefOf(policy), FinanceReducer.Version).Value;

    private static FinanceProjectionState Project(ScenarioRun run, PolicyConfig policy)
    {
        var context = Pin(run, policy);
        return FinanceReducer.ApplyAll(FinanceReducer.Initial(run, context).Value, run.Intents, context).Value;
    }

    private static PaymentIntent Intent(
        ScenarioRun run, long sequence, long time, string source, string dest, long amount, SyntheticTruth truth,
        bool newBeneficiary = false, bool sanctions = false, string currency = "USD") =>
        new(run.RunId, $"T-{sequence}", sequence, time, source.Split('/')[0], source, dest.Split('/')[0], dest,
            new Money(currency, amount), truth, new PaymentFeatures(newBeneficiary, sanctions));

    private static PaymentIntent Retime(PaymentIntent i, long time) =>
        new(i.RunId, i.IntentId, i.Sequence, time, i.SourceBankId, i.SourceAccountId, i.DestBankId, i.DestAccountId,
            i.Amount, i.Truth, i.Features);
}
