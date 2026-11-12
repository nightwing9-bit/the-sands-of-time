using Marrow.Timeline;
using Microsoft.Extensions.DependencyInjection;
using Revenant.Contracts;
using Revenant.Intelligence;
using Revenant.Payments;
using Revenant.Timeline;

namespace Revenant.Api;

public static class RevenantV4WorkerRegistration
{
    /// <summary>Uses the ledger writer for V4 dispatch and the existing restricted Timeline engine for synchronization.</summary>
    public static IServiceCollection AddRevenantV4Worker(this IServiceCollection services)
    {
        if (!services.Any(service => service.ServiceType == typeof(RevenantLedgerDataSource)))
            return services;

        services.AddSingleton(sp => new V4SimulationStore(
            sp.GetRequiredService<RevenantLedgerDataSource>().DataSource,
            ledger: sp.GetRequiredService<PaymentLedgerStore>(),
            model: new(() => BehavioralRiskScorerV2.Load(
                Path.Combine(AppContext.BaseDirectory, "Intelligence", "Artifacts", BehavioralRiskScorerV2.ArtifactDirectory),
                BehavioralRiskScorerV2.FeatureSchema, BehavioralRiskScorerV2.PublishedPin))));
        services.AddSingleton<V4BranchExecution>(sp => new(sp.GetRequiredService<V4SimulationStore>(),
            sp.GetRequiredService<ITimelineEngine<ObservedFinanceState, ObservedFinanceInput, ObservedFinanceContext>>()));
        services.AddSingleton(sp => new RevenantV4Worker(sp.GetRequiredService<V4SimulationStore>(),
            sp.GetRequiredService<V4BranchExecution>(), sp.GetRequiredService<RevenantLedgerOptions>()));
        return services;
    }
}

public sealed record RevenantV4DispatchReceipt(int Steps, long LastFact, string PrefixDigest, string? BlockedCode);

public sealed class RevenantV4Worker
{
    public const int MaxStepsPerInvocation = 4096;

    private readonly V4SimulationStore store;
    private readonly V4BranchExecution branch;
    private readonly string tenant;

    internal RevenantV4Worker(V4SimulationStore store, V4BranchExecution branch, RevenantLedgerOptions options)
    {
        this.store = store;
        this.branch = branch;
        tenant = options.TenantId;
    }

    /// <summary>Advances a bounded number of scheduler steps. Zero steps reconciles committed SQL facts after interruption.</summary>
    public async Task<RevenantV4DispatchReceipt> DispatchAsync(ScenarioRunId execution, int steps, CancellationToken ct)
    {
        if (execution.IsEmpty)
            throw new ArgumentException("Expected a physical V4 execution ID.", nameof(execution));
        ArgumentOutOfRangeException.ThrowIfNegative(steps);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(steps, MaxStepsPerInvocation);

        var applied = 0;
        string? blocked = null;
        while (applied < steps)
        {
            var step = await store.AdvanceOneStepAsync(tenant, execution, ct);
            if (step.Kind is not (V4StepKind.ProcessedCommand or V4StepKind.Sealed))
            {
                blocked = step.Command?.OutcomeCode ?? step.Kind.ToString();
                break;
            }
            applied++;
        }

        var synchronized = await branch.SynchronizeAsync(tenant, execution, ct);
        return new(applied, synchronized.LastFact, synchronized.PrefixDigest, blocked);
    }
}
