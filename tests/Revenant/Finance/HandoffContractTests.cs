using System.Reflection;
using NetArchTest.Rules;
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
    public void Cost_model_v1_is_stated_in_usd_minor_units_as_the_spec_defines_it()
    {
        // Spec 05 and Spec 09 design: 250 and 10 are USD *minor units* (USD 2.50 and USD 0.10), capped at 120 minutes.
        var costs = CounterfactualCostModel.V1;
        Assert.Equal(("USD", 250L, 10L, 120), (costs.Currency, costs.FalsePositiveCostMinor, costs.DelayCostPerMinuteMinor, costs.DelayCapMinutes));
        Assert.Equal(typeof(long), typeof(CounterfactualCostModel).GetProperty(nameof(CounterfactualCostModel.FalsePositiveCostMinor))!.PropertyType);
        Assert.Equal(typeof(long), typeof(CounterfactualCostModel).GetProperty(nameof(CounterfactualCostModel.DelayCostPerMinuteMinor))!.PropertyType);
    }

    [Fact]
    public void Pure_finance_code_has_no_persistence_host_or_other_domain_dependencies()
    {
        // Spec 07 task 7.2 introduced domain persistence (Revenant.Payments) and the API registration (Revenant.Api) into this
        // assembly, so the no-database-driver assertion now covers exactly the pure finance code: the reducer, scenario and
        // Timeline adapter namespaces (task 7.5 adds the V2 reducer/codecs there, the pure review rules in Revenant.Cases, and keeps
        // the database-reading projector in Revenant.Projection out of it). The whole assembly still may not reference shared Infrastructure or the game domain.
        var assembly = typeof(FinanceReducer).Assembly;
        var names = assembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToArray();
        Assert.DoesNotContain(names, n => n.StartsWith("Marrow.Infrastructure", StringComparison.Ordinal)
            || n.StartsWith("GhostLap", StringComparison.Ordinal));

        var pure = Types.InAssembly(assembly).That()
            .ResideInNamespaceStartingWith("Revenant.Finance").Or()
            .ResideInNamespaceStartingWith("Revenant.Scenario").Or()
            .ResideInNamespaceStartingWith("Revenant.Cases").Or()
            .ResideInNamespaceStartingWith("Revenant.Timeline");
        var forbidden = new[]
        {
            "Npgsql", "Microsoft.AspNetCore", "Microsoft.Extensions", "Marrow.Infrastructure", "GhostLap", "Revenant.Payments", "Revenant.Api", "Revenant.Projection",
        };
        var result = pure.ShouldNot().HaveDependencyOnAny(forbidden).GetResult();
        Assert.True(result.IsSuccessful, "Pure finance types depend on persistence or host code: " + string.Join(", ", result.FailingTypeNames ?? []));
        Assert.NotEmpty(pure.GetTypes());
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
