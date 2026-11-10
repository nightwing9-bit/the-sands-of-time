using Xunit;

namespace TheSandsOfTime.Architecture.Tests;

public sealed class ModuleArchitectureTests
{
    [Fact]
    public void Kernel_references_nothing_beyond_the_base_class_library()
    {
        var kernel = ModuleGraph.Get(ModuleGraph.Kernel);

        Assert.NotEmpty(kernel.GetTypes()); // guard against a vacuous pass on an empty assembly
        AssertNone(ModuleDependencyRules.KernelViolations(kernel));
    }

    [Fact]
    public void Timeline_references_Kernel_only()
    {
        AssertNone(ModuleDependencyRules.TimelineViolations(ModuleGraph.Get(ModuleGraph.Timeline)));
    }

    [Fact]
    public void Infrastructure_knows_no_domain_and_no_host()
    {
        AssertNone(ModuleDependencyRules.InfrastructureViolations(ModuleGraph.Get(ModuleGraph.Infrastructure)));
    }

    [Fact]
    public void Revenant_assemblies_do_not_reach_Infrastructure_hosts_or_GhostLap()
    {
        var domains = ModuleGraph.Domain("Revenant").ToList();
        Assert.NotEmpty(domains);
        AssertNone(domains.SelectMany(d => ModuleDependencyRules.DomainViolations(d, "Revenant", "GhostLap")));
    }

    [Fact]
    public void GhostLap_assemblies_do_not_reach_Infrastructure_hosts_or_Revenant()
    {
        var domains = ModuleGraph.Domain("GhostLap").ToList();
        Assert.NotEmpty(domains);
        AssertNone(domains.SelectMany(d => ModuleDependencyRules.DomainViolations(d, "GhostLap", "Revenant")));
    }

    [Fact]
    public void Game_domain_has_no_rendering_dependency()
    {
        AssertNone(ModuleGraph.Domain("GhostLap").SelectMany(ModuleDependencyRules.GameRenderingViolations));
    }

    [Fact]
    public void Finance_domain_has_no_game_dependency()
    {
        AssertNone(ModuleGraph.Domain("Revenant").SelectMany(ModuleDependencyRules.FinanceToGameViolations));
    }

    [Fact]
    public void No_assembly_references_a_host()
    {
        AssertNone(ModuleDependencyRules.HostReferenceViolations());
    }

    /// <summary>
    /// The test-only Timeline host (E2E.Backend) and test assemblies must never become a dependency of shipped code, otherwise the
    /// fixture routes could reach a product host. Product assemblies are all the project assemblies in the output directory.
    /// </summary>
    [Fact]
    public void No_product_assembly_references_a_test_only_assembly()
    {
        var violations = ModuleGraph.ProjectAssemblies.Values.SelectMany(assembly =>
            ModuleGraph.DirectReferences(assembly)
                .Where(reference => reference == "E2E.Backend" || reference.EndsWith(".Tests", StringComparison.Ordinal))
                .Select(reference => $"{assembly.GetName().Name} -> {reference}"));
        AssertNone(violations);
    }
    [Fact]
    public void Expected_modules_are_present_as_compiled_assemblies()
    {
        string[] expected =
        [
            "Marrow.Kernel", "Marrow.Timeline", "Marrow.Infrastructure",
            "Revenant.Contracts", "GhostLap.Contracts",
            "Hosts.Api", "Hosts.Realtime", "Hosts.Worker",
        ];

        Assert.Empty(expected.Except(ModuleGraph.ProjectAssemblies.Keys));
    }

    /// <summary>
    /// Detector sensitivity on real binaries: Hosts.Api legitimately uses Marrow.Infrastructure, so the
    /// "domains must not reference Infrastructure" rule applied to it has to report that edge.
    /// If this ever passes with no violations, the reference-table reader has stopped seeing edges.
    /// </summary>
    [Fact]
    public void Domain_rule_reports_a_real_Infrastructure_edge_when_applied_to_a_host()
    {
        var api = ModuleGraph.Get("Hosts.Api");

        var violations = ModuleDependencyRules.DomainViolations(api, "Revenant", "GhostLap").ToList();

        Assert.Contains(violations, v => v.Contains("Hosts.Api -> Marrow.Infrastructure", StringComparison.Ordinal));
    }

    private static void AssertNone(IEnumerable<string> violations)
    {
        var list = violations.Distinct().ToList();
        Assert.True(list.Count == 0, "Forbidden dependency edges:" + Environment.NewLine + string.Join(Environment.NewLine, list));
    }
}
