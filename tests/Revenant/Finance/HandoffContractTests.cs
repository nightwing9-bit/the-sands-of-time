using System.Reflection;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Timeline;
using Revenant.Tests.Scenario;
using Xunit;
using Xunit.Abstractions;

namespace Revenant.Tests.Finance;

/// <summary>
/// Task 5.5: frozen handoff surface that specs 07, 08 and 09 consume unchanged.
/// **Validates: Requirements R05.05, R05.08, R05.09, R05.11**
/// </summary>
public sealed class HandoffContractTests(ITestOutputHelper output)
{
    private static readonly ScenarioRunId RunA = new(Guid.Parse("00000000-0000-0000-0000-0000000005c5"));

    [Fact]
    public void Reducer_public_surface_and_version_are_frozen()
    {
        Assert.Equal("revenant-finance-reducer/1", FinanceReducer.Version);
        var signatures = typeof(FinanceReducer).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(m => $"{m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})")
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(
            ["Apply(FinanceProjectionState,PaymentIntent,FinanceContext)",
             "ApplyAll(FinanceProjectionState,IEnumerable`1,FinanceContext)",
             "Initial(ScenarioRun,FinanceContext)"],
            signatures);
        Assert.Equal(
            ["Compare"],
            typeof(CounterfactualMetrics).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName).Select(m => m.Name).ToArray());
    }

    [Fact]
    public void Metrics_record_keeps_its_named_fields_with_pending_fraud_reported_separately()
    {
        string[] expected =
        [
            "LabelSchemaVersion", "CostModelVersion", "HorizonUtcMs", "AlignedSequence", "FraudPreventedCount",
            "FraudPreventedExposureMinor", "FraudDeferredAtHorizonCount", "LegitimateDelayedCount",
            "FalsePositiveCostMinor", "IncrementalDelayCostMinor", "NetEffectMinor", "DecisionDifferenceCount",
        ];
        var actual = typeof(CounterfactualMetrics).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.Name != "EqualityContract").Select(p => p.Name).ToArray();
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
        Assert.Equal("counterfactual-cost-v1", CounterfactualCostModel.V1.Version);
    }

    [Fact]
    public void Domain_assembly_has_no_persistence_host_or_clock_dependencies()
    {
        var names = typeof(FinanceReducer).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToArray();
        Assert.DoesNotContain(names, n => n.StartsWith("Npgsql", StringComparison.Ordinal)
            || n.StartsWith("Marrow.Infrastructure", StringComparison.Ordinal)
            || n.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
            || n.StartsWith("GhostLap", StringComparison.Ordinal));
    }

    [Fact]
    public void Pending_fraud_is_excluded_from_terminal_prevention_and_avoided_exposure_and_hash_report_is_stable()
    {
        var run = FridayRingScenarioTests.Create(42, RunA);
        var v7 = Project(run, PolicyCatalog.V7);
        var v8 = Project(run, PolicyCatalog.V8);
        var again = Project(run, PolicyCatalog.V8);
        var m = CounterfactualMetrics.Compare(v7, v8, CounterfactualCostModel.V1).Value;

        Assert.Equal(10, m.FraudDeferredAtHorizonCount);
        Assert.Equal(0, m.FraudPreventedCount);
        Assert.Equal(0, m.FraudPreventedExposureMinor);
        // Net effect never monetizes the pending principal: it is exactly minus the two legitimate-delay costs.
        Assert.Equal(-(m.FalsePositiveCostMinor + m.IncrementalDelayCostMinor), m.NetEffectMinor);
        // Same inputs and pins give the identical state for the consumer (no hidden clock/randomness).
        Assert.Equal(Bytes(v8), Bytes(again));
        Assert.Equal(v7.TotalPostedMinor, v8.TotalPostedMinor);

        output.WriteLine($"run={run.RunId} exogenousFactsHash={run.ExogenousFactsHash}");
        output.WriteLine($"v7 policy={PolicyCatalog.V7.ContentHash} v8 policy={PolicyCatalog.V8.ContentHash}");
        output.WriteLine($"v7StateSha={Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Bytes(v7)))} v8StateSha={Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Bytes(v8)))}");
        output.WriteLine($"metrics: {m}");
    }

    private static byte[] Bytes(FinanceProjectionState state) => FinanceStateCodec.Instance.EncodeCanonical(state);

    private static FinanceProjectionState Project(ScenarioRun run, PolicyConfig policy)
    {
        var context = FinanceContext.Pin(run.Manifest, PolicyCatalog.RefOf(policy), FinanceReducer.Version).Value;
        return FinanceReducer.ApplyAll(FinanceReducer.Initial(run, context).Value, run.Intents, context).Value;
    }
}
