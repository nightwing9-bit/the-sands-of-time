using System.Reflection;
using NetArchTest.Rules;

namespace TheSandsOfTime.Architecture.Tests;

/// <summary>
/// Dependency rules for the modular monolith. Each rule returns human-readable violations so a failing
/// test names the offending edge. Rules read compiled assemblies (assembly reference table, and type
/// level dependencies through NetArchTest), never folder names or project files.
/// </summary>
internal static class ModuleDependencyRules
{
    // Namespaces that Kernel and Timeline must not depend on at the type level.
    private static readonly string[] FrameworkNamespaces =
    [
        "Microsoft.AspNetCore", // includes Microsoft.AspNetCore.SignalR
        "Microsoft.EntityFrameworkCore",
        "Microsoft.Extensions",
        "Npgsql",
        "StackExchange",
    ];

    /// <summary>Kernel: BCL only. No other Marrow, domain, host, ASP.NET, EF, SignalR or driver assembly.</summary>
    public static IEnumerable<string> KernelViolations(Assembly kernel) =>
        NonBaseClassLibraryReferences(kernel)
            .Select(r => $"{Name(kernel)} -> {r} (Kernel may reference the base class library only)")
            .Concat(TypeDependencyViolations(kernel,
                [.. FrameworkNamespaces, "Marrow.Timeline", "Marrow.Infrastructure", "Revenant", "GhostLap", "Hosts"]));

    /// <summary>Timeline: Kernel plus the base class library only.</summary>
    public static IEnumerable<string> TimelineViolations(Assembly timeline) =>
        NonBaseClassLibraryReferences(timeline)
            .Where(r => r != ModuleGraph.Kernel)
            .Select(r => $"{Name(timeline)} -> {r} (Timeline may reference Marrow.Kernel only)")
            .Concat(TypeDependencyViolations(timeline,
                [.. FrameworkNamespaces, "Marrow.Infrastructure", "Revenant", "GhostLap", "Hosts"]));

    /// <summary>Shared Infrastructure is plumbing: it may not know any domain or any host.</summary>
    public static IEnumerable<string> InfrastructureViolations(Assembly infrastructure) =>
        ForbiddenProjectReferences(infrastructure, IsDomainOrHost)
            .Concat(TypeDependencyViolations(infrastructure, ["Revenant", "GhostLap", "Hosts"]));

    /// <summary>
    /// A domain assembly may consume Kernel, Timeline and its own domain assemblies. It may not reach
    /// shared Infrastructure, a host, or the other domain, directly or through another project assembly.
    /// </summary>
    public static IEnumerable<string> DomainViolations(Assembly domain, string ownPrefix, string otherPrefix)
    {
        bool Forbidden(string n) =>
            n == ModuleGraph.Infrastructure
            || n.StartsWith("Hosts.", StringComparison.Ordinal)
            || n.StartsWith(otherPrefix + ".", StringComparison.Ordinal);

        return ForbiddenProjectReferences(domain, Forbidden)
            .Concat(TypeDependencyViolations(domain, ["Marrow.Infrastructure", "Hosts", otherPrefix]));
    }

    /// <summary>Game domain must not use a rendering toolkit; finance domain must not use the game domain.</summary>
    public static IEnumerable<string> GameRenderingViolations(Assembly game) =>
        ModuleGraph.DirectReferences(game)
            .Where(r => ModuleGraph.StartsWithAny(r, ModuleGraph.RenderingPrefixes))
            .Select(r => $"{Name(game)} -> {r} (game domain must not depend on rendering)")
            .Concat(TypeDependencyViolations(game, ModuleGraph.RenderingPrefixes));

    public static IEnumerable<string> FinanceToGameViolations(Assembly finance) =>
        ForbiddenProjectReferences(finance, n => n.StartsWith("GhostLap.", StringComparison.Ordinal))
            .Concat(TypeDependencyViolations(finance, ["GhostLap"]));

    /// <summary>Hosts compose implementations. No other module may reference a host, and hosts may not reference each other.</summary>
    public static IEnumerable<string> HostReferenceViolations() =>
        ModuleGraph.ProjectAssemblies.Values.SelectMany(a =>
            ModuleGraph.DirectReferences(a)
                .Where(r => r.StartsWith("Hosts.", StringComparison.Ordinal) && r != Name(a))
                .Select(r => $"{Name(a)} -> {r} (nothing may reference a host assembly)"));

    private static IEnumerable<string> ForbiddenProjectReferences(Assembly assembly, Func<string, bool> forbidden) =>
        ModuleGraph.DirectReferences(assembly)
            .Where(forbidden)
            .Select(r => $"{Name(assembly)} -> {r}")
            .Concat(ModuleGraph.ProjectClosure(assembly)
                .Where(forbidden)
                .Where(r => !ModuleGraph.DirectReferences(assembly).Contains(r))
                .Select(r => $"{Name(assembly)} -> ... -> {r} (transitive)"));

    private static IEnumerable<string> NonBaseClassLibraryReferences(Assembly assembly) =>
        ModuleGraph.DirectReferences(assembly).Where(r => !ModuleGraph.IsBaseClassLibrary(r));

    private static IEnumerable<string> TypeDependencyViolations(Assembly assembly, string[] forbiddenNamespaces)
    {
        var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(forbiddenNamespaces).GetResult();
        return result.IsSuccessful
            ? []
            : result.FailingTypeNames.Select(t => $"{t} depends on one of [{string.Join(", ", forbiddenNamespaces)}]");
    }

    private static bool IsDomainOrHost(string n) =>
        n.StartsWith("Revenant.", StringComparison.Ordinal)
        || n.StartsWith("GhostLap.", StringComparison.Ordinal)
        || n.StartsWith("Hosts.", StringComparison.Ordinal);

    private static string Name(Assembly a) => a.GetName().Name!;
}
