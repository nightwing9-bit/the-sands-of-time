using System.Reflection;

namespace TheSandsOfTime.Architecture.Tests;

/// <summary>
/// Reads the compiled assemblies in the test output directory. Rules are evaluated against the
/// assembly reference tables and type metadata of those binaries, not against folder or project names.
/// </summary>
internal static class ModuleGraph
{
    public const string Kernel = "Marrow.Kernel";
    public const string Timeline = "Marrow.Timeline";
    public const string Infrastructure = "Marrow.Infrastructure";

    private static readonly string[] ProjectPrefixes = ["Marrow.", "Revenant.", "GhostLap.", "Hosts."];

    // Assemblies that belong to the .NET base class library and are allowed in dependency-free modules.
    private static readonly string[] BaseClassLibraryPrefixes = ["System", "netstandard", "mscorlib"];

    // Assembly/namespace prefixes that mean "rendering / presentation toolkit".
    public static readonly string[] RenderingPrefixes =
    [
        "Pixi", "PixiJS", "Rendering", "Marrow.Rendering", "Revenant.Rendering", "GhostLap.Rendering",
        "Microsoft.AspNetCore.Components", "Microsoft.Maui", "Microsoft.Xna",
        "SkiaSharp", "Avalonia", "Silk.NET", "OpenTK",
    ];

    private static readonly Lazy<IReadOnlyDictionary<string, Assembly>> Loaded = new(LoadProjectAssemblies);

    public static IReadOnlyDictionary<string, Assembly> ProjectAssemblies => Loaded.Value;

    public static Assembly Get(string name) =>
        ProjectAssemblies.TryGetValue(name, out var assembly)
            ? assembly
            : throw new InvalidOperationException(
                $"Assembly '{name}' was not found in '{AppContext.BaseDirectory}'. Reference its project from Architecture.Tests.csproj.");

    public static IEnumerable<Assembly> Domain(string prefix) =>
        ProjectAssemblies.Values.Where(a => a.GetName().Name!.StartsWith(prefix + ".", StringComparison.Ordinal));

    public static IEnumerable<Assembly> Hosts() => Domain("Hosts");

    public static IEnumerable<Assembly> NonHosts() =>
        ProjectAssemblies.Values.Where(a => !a.GetName().Name!.StartsWith("Hosts.", StringComparison.Ordinal));

    public static string[] DirectReferences(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(r => r.Name!).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    public static bool IsBaseClassLibrary(string assemblyName) => StartsWithAny(assemblyName, BaseClassLibraryPrefixes);

    public static bool StartsWithAny(string name, IEnumerable<string> prefixes) =>
        prefixes.Any(p => name == p || name.StartsWith(p + ".", StringComparison.Ordinal));

    /// <summary>Project assemblies reachable from <paramref name="assembly"/> through compiled references (excluding itself).</summary>
    public static IReadOnlySet<string> ProjectClosure(Assembly assembly)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<Assembly>();
        queue.Enqueue(assembly);
        while (queue.Count > 0)
        {
            foreach (var reference in DirectReferences(queue.Dequeue()))
            {
                if (ProjectAssemblies.TryGetValue(reference, out var next) && seen.Add(reference))
                {
                    queue.Enqueue(next);
                }
            }
        }

        seen.Remove(assembly.GetName().Name!);
        return seen;
    }

    private static IReadOnlyDictionary<string, Assembly> LoadProjectAssemblies()
    {
        var result = new SortedDictionary<string, Assembly>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.EndsWith(".Tests", StringComparison.Ordinal) || !ProjectPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
            {
                continue;
            }

            result[name] = Assembly.Load(new AssemblyName(name));
        }

        return result;
    }
}
