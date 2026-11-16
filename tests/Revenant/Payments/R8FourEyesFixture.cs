using Marrow.Kernel.Canonical;
using Microsoft.Extensions.DependencyInjection;
using Revenant.Cases;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Intelligence;
using Revenant.Payments;
using Revenant.Scenario;
using Revenant.Timeline;

namespace Revenant.Tests.Payments;

/// <summary>Prepares a dedicated payment through the same bound V4 services as the real R8 API.</summary>
public static class R8FourEyesFixture
{
    public static async Task PrepareAsync(IServiceProvider services, string tenant, string owner, CancellationToken ct)
    {
        const long origin = 1_789_203_300_000;
        const long at = origin + V4ReviewCounts.WarmupMs;
        const long principal = 1_500_000;
        const string intentId = "P-FOUR-EYES";
        var id = new ScenarioRunId(Guid.NewGuid());
        var run = BehavioralEpisodeScenario.Create(id, 42, origin, V4ReviewCounts.WarmupMs + 900_000, [2, 2, 2, 2, 2, 2]);
        var payer = run.Manifest.Accounts.First(a => a.BankId == "BANK-A" && a.OpeningBalance.MinorUnits >= principal);
        var payee = run.Manifest.Accounts.First(a => a.BankId == "BANK-B");
        var intent = new PaymentIntent(id, intentId, 1, at, payer.BankId, payer.AccountId, payee.BankId, payee.AccountId,
            Money.Usd(principal), SyntheticTruth.Legitimate, new(false, true));
        var context = new ObservedFinanceContext(run.Manifest.ScenarioVersion, run.Manifest.ContentHash, origin,
            PolicyCatalog.RefOf(PolicyCatalog.V7), ReviewPolicy.Version, ObservedRiskMode.ModelRequired,
            BehavioralRiskScorerV2.FeatureSchema, BehavioralRiskScorerV2.PublishedPin, [], []);
        var branch = services.GetRequiredService<V4BranchExecution>();
        var store = services.GetRequiredService<V4SimulationStore>();
        var ledger = services.GetRequiredService<PaymentLedgerStore>();
        await branch.OpenSourceAsync(tenant, owner, run, context, ct);
        for (var boundary = origin + ObservedFinanceVersion.BucketMs; boundary <= at; boundary += ObservedFinanceVersion.BucketMs)
        {
            if ((await store.SealAsync(tenant, id, boundary, ct)).Kind != V4StepKind.Sealed)
                throw new InvalidOperationException($"Four-eyes fixture could not seal boundary {boundary}.");
        }

        await DispatchAsync(new("four-eyes:intake", V4CommandClass.Exogenous, "PaymentIntent", ScenarioSchemas.InputSchemaVersion,
            [.. PaymentIntentCodec.Instance.EncodeCanonical(intent)], at, at), PaymentStatus.Initiated);
        await DispatchAsync(new("four-eyes:screen", V4CommandClass.DerivedOpportunity, "Screening", "v4-payment-opportunity/1",
            [.. new CanonicalWriter().WriteInt32(1).WriteString(intentId).ToArray()], at, at), PaymentStatus.Held);

        // Synchronization also checks the restored Timeline against the committed SQL facts.
        var held = await branch.SynchronizeAsync(tenant, id, ct);
        var account = held.Ledger.Accounts[payer.AccountId];
        var review = await ledger.GetCaseAsync(tenant, id, intentId, ct);
        if (review is not { Status: CaseStatus.Open, PaymentStatus: PaymentStatus.Held, RequiredApprovals: 2, Version: > 0 }
            || review.Approvals.Count != 0 || review.AmountMinor != principal || review.RunId != id
            || account.PostedMinor != payer.OpeningBalance.MinorUnits || account.ReservedMinor != principal
            || held.LastSealedBoundaryMs != at)
            throw new InvalidOperationException("The V4 scheduler did not produce the expected funded four-eyes hold.");

        async Task DispatchAsync(V4CommandRequest request, PaymentStatus expected)
        {
            var admitted = await store.AdmitAsync(tenant, id, request, ct);
            var step = await store.AdvanceOneStepAsync(tenant, id, ct);
            if (step.Kind != V4StepKind.ProcessedCommand || step.Command is not { Status: V4CommandStatus.Completed } command
                || command.Sequence != admitted.Sequence || command.OutcomeCode != expected.ToString())
                throw new InvalidOperationException($"Four-eyes fixture {request.Kind} expected {expected}, got {step.Kind}/{step.Command?.OutcomeCode}.");
        }
    }
}
