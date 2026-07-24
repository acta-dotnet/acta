using System.Reflection;
using System.Text.RegularExpressions;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance;

/// <summary>
/// Store-method coverage gate: every method on every internal store interface is declared by at least
/// one spec's <c>[CoversStoreMethod]</c> attribute.
/// </summary>
public sealed class StoreContractCoverageTests
{
    // Every store method is covered by a live conformance spec; there is no allowlist backlog.
    private static readonly HashSet<string> NotYetCovered = new(StringComparer.Ordinal);

    private static readonly Regex StoreInterfaceName = new(@"^I\w+Store$", RegexOptions.Compiled);

    /// <summary>
    /// Asserts every store method is covered, no attribute names a method that does not exist, and the
    /// allowlist has no stale entries.
    /// </summary>
    [Fact]
    public void Every_store_method_has_a_covering_contract()
    {
        var methods = StoreInterfaces()
            .SelectMany(s => DeclaredMethods(s).Select(m => $"{s.FullName}.{m.Name}"))
            .ToHashSet(StringComparer.Ordinal);

        var covered = typeof(IConformanceFixture)
            .Assembly.GetTypes()
            .Where(HarnessReflection.IsCandidateContractSpec)
            .SelectMany(t => t.GetCustomAttributes<CoversStoreMethodAttribute>())
            .Select(c => c.Identity)
            .ToHashSet(StringComparer.Ordinal);

        var missing = methods.Where(m => !covered.Contains(m) && !NotYetCovered.Contains(m)).OrderBy(x => x).ToList();
        var unknown = covered.Where(c => !methods.Contains(c)).OrderBy(x => x).ToList();
        var stale = NotYetCovered.Where(a => covered.Contains(a) || !methods.Contains(a)).OrderBy(x => x).ToList();

        Assert.True(missing.Count == 0, "Store methods with no covering [CoversStoreMethod] spec:\n" + string.Join("\n", missing));
        Assert.True(unknown.Count == 0, "[CoversStoreMethod] declarations naming no existing store method:\n" + string.Join("\n", unknown));
        Assert.True(stale.Count == 0, "Allowlist entries now covered or non-existent (remove them):\n" + string.Join("\n", stale));
    }

    /// <summary>
    /// Store methods use unique names (no overloads) so {interface}.{method} stays a stable identity for
    /// coverage tagging, binding checks, and the generated inventory.
    /// </summary>
    [Fact]
    public void Store_methods_are_not_overloaded()
    {
        var overloaded = StoreInterfaces()
            .SelectMany(s =>
                DeclaredMethods(s)
                    .GroupBy(m => m.Name, StringComparer.Ordinal)
                    .Where(g => g.Count() > 1)
                    .Select(g => $"{s.FullName}.{g.Key}")
            )
            .OrderBy(x => x)
            .ToList();

        Assert.True(overloaded.Count == 0, "Overloaded store methods (rename to unique names):\n" + string.Join("\n", overloaded));
    }

    // The internal store ports: I*Store interfaces under Acta.Features/Acta.Services in the core
    // assembly. The property-only IActaStore composite sits in the root Acta namespace, so it is
    // excluded by construction.
    internal static List<Type> StoreInterfaces() =>
        typeof(ActaServiceCollectionExtensions)
            .Assembly.GetTypes()
            .Where(t =>
                t.IsInterface
                && StoreInterfaceName.IsMatch(t.Name)
                && t.Namespace is { } ns
                && (ns.StartsWith("Acta.Features.", StringComparison.Ordinal) || ns.StartsWith("Acta.Services.", StringComparison.Ordinal))
            )
            .ToList();

    internal static IEnumerable<MethodInfo> DeclaredMethods(Type store) => store.GetMethods().Where(m => !m.IsSpecialName);
}
