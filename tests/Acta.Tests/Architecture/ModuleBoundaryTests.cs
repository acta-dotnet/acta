using System.Reflection;
using System.Text.RegularExpressions;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Architecture;

/// <summary>
/// Module-boundary gate over the runtime assembly: a type in Modules/X may not take another
/// module's store port in its constructor. Store ports are persistence internals, not module APIs;
/// cross-module collaboration goes through declared seams (IJobSubmission, IAlertSink, the domain
/// facades) or the durable event ledger. Survivors of the restructure are declared one by one in
/// <see cref="Baseline"/> so they shrink but never silently grow.
/// </summary>
public sealed class ModuleBoundaryTests
{
    /// <summary>"{ConsumerType}: {StorePort}" pairs allowed to cross modules, with the reason.</summary>
    private static readonly HashSet<string> Baseline = new(StringComparer.Ordinal) { };

    [Fact]
    public void Modules_do_not_take_other_modules_store_ports()
    {
        var runtime = typeof(ActaServiceCollectionExtensions).Assembly;
        var violations = new List<string>();

        foreach (var type in runtime.GetTypes())
        {
            if (ModuleOf(type.Namespace) is not { } consumerModule)
            {
                continue;
            }

            foreach (var constructor in type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                foreach (var parameter in constructor.GetParameters())
                {
                    var parameterType = parameter.ParameterType;
                    if (
                        !parameterType.IsInterface
                        || !parameterType.Name.StartsWith("I", StringComparison.Ordinal)
                        || !parameterType.Name.EndsWith("Store", StringComparison.Ordinal)
                    )
                    {
                        continue;
                    }

                    if (ModuleOf(parameterType.Namespace) is not { } ownerModule || ownerModule == consumerModule)
                    {
                        continue;
                    }

                    var pair = $"{type.FullName}: {parameterType.Name}";
                    if (!Baseline.Contains(pair))
                    {
                        violations.Add($"{pair} ({consumerModule} takes a {ownerModule}-owned store port)");
                    }
                }
            }
        }

        Assert.True(violations.Count == 0, "Cross-module store injection:\n" + string.Join("\n", violations));
    }

    /// <summary>
    /// The constructor gate's service-locator escape hatch, closed: module source may not resolve
    /// another module's store port via GetService/GetRequiredService either.
    /// </summary>
    [Fact]
    public void Modules_do_not_service_locate_other_modules_store_ports()
    {
        var runtime = typeof(ActaServiceCollectionExtensions).Assembly;
        var storeModules = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var type in runtime.GetTypes())
        {
            if (
                type.IsInterface
                && type.Name.StartsWith("I", StringComparison.Ordinal)
                && type.Name.EndsWith("Store", StringComparison.Ordinal)
                && ModuleOf(type.Namespace) is { } module
            )
            {
                storeModules[type.Name] = module;
            }
        }

        var modulesRoot = Path.Combine(IntegrationConfig.FindRepoRoot(), "src", "Acta.Runtime", "Modules");
        var locate = new Regex(@"Get(?:Required)?Service<(?<store>I\w+Store)>", RegexOptions.Compiled);
        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(modulesRoot, "*.cs", SearchOption.AllDirectories))
        {
            var consumerModule = Path.GetRelativePath(modulesRoot, file).Split(Path.DirectorySeparatorChar)[0];
            foreach (Match match in locate.Matches(File.ReadAllText(file)))
            {
                var store = match.Groups["store"].Value;
                if (storeModules.TryGetValue(store, out var owner) && owner != consumerModule)
                {
                    violations.Add($"{Path.GetFileName(file)}: {consumerModule} service-locates {owner}-owned {store}");
                }
            }
        }

        Assert.True(violations.Count == 0, "Cross-module store resolution:\n" + string.Join("\n", violations));
    }

    /// <summary>
    /// The declared module graph: every cross-module reference in module source (Operations
    /// included) must match one of these edges and may target only the referenced module's
    /// <c>Api</c> namespace. No baseline: the graph is clean, so a new edge is a design decision
    /// that lands here and in design.md together.
    /// </summary>
    private static readonly HashSet<string> ApiEdges = new(StringComparer.Ordinal)
    {
        "Alerting -> Execution",
        "Outbox -> Execution",
        "Operations -> Execution",
    };

    [Fact]
    public void Cross_module_references_follow_the_declared_graph()
    {
        var modulesRoot = Path.Combine(IntegrationConfig.FindRepoRoot(), "src", "Acta.Runtime", "Modules");
        var reference = new Regex(@"Acta\.Modules\.(?<module>\w+)(?:\.(?<sub>\w+))?", RegexOptions.Compiled);
        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(modulesRoot, "*.cs", SearchOption.AllDirectories))
        {
            var consumer = Path.GetRelativePath(modulesRoot, file).Split(Path.DirectorySeparatorChar)[0];
            foreach (Match match in reference.Matches(File.ReadAllText(file)))
            {
                var target = match.Groups["module"].Value;
                if (target == consumer)
                {
                    continue;
                }

                var edge = $"{consumer} -> {target}";
                if (!ApiEdges.Contains(edge))
                {
                    violations.Add($"{Path.GetFileName(file)}: undeclared edge {edge}");
                }
                else if (match.Groups["sub"].Value != "Api")
                {
                    violations.Add($"{Path.GetFileName(file)}: {edge} must target {target}.Api, not {match.Value}");
                }
            }
        }

        Assert.True(violations.Count == 0, "Module reference graph violations:\n" + string.Join("\n", violations));
    }

    /// <summary>
    /// The declared graph is acyclic (the proposal's cycle-freedom rule): a new edge that closes a
    /// loop between modules fails here even before any source references it, so a cycle can never
    /// be legalized by declaration.
    /// </summary>
    [Fact]
    public void Declared_module_graph_is_free_of_cycles()
    {
        var edges = ApiEdges.Select(e => e.Split(" -> ")).ToLookup(parts => parts[0], parts => parts[1]);

        var finished = new HashSet<string>(StringComparer.Ordinal);
        var path = new List<string>();

        void Visit(string module)
        {
            if (finished.Contains(module))
            {
                return;
            }
            var cycleStart = path.IndexOf(module);
            if (cycleStart >= 0)
            {
                Assert.Fail("Module dependency cycle: " + string.Join(" -> ", path.Skip(cycleStart).Append(module)));
            }

            path.Add(module);
            foreach (var target in edges[module])
            {
                Visit(target);
            }
            path.RemoveAt(path.Count - 1);
            finished.Add(module);
        }

        foreach (var module in edges.Select(g => g.Key))
        {
            Visit(module);
        }
    }

    private static string? ModuleOf(string? ns)
    {
        const string prefix = "Acta.Modules.";
        if (ns is null || !ns.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = ns[prefix.Length..];
        var dot = rest.IndexOf('.');
        return dot < 0 ? rest : rest[..dot];
    }
}
