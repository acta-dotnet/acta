using System.Reflection;
using System.Text.RegularExpressions;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance;

/// <summary>
/// The store ports the provider binding gates read, and the rule that keeps their method names unique.
/// </summary>
public sealed partial class StoreContractCoverageTests
{
    private static readonly Regex StoreInterfaceName = MyRegex();

    /// <summary>
    /// Store methods use unique names (no overloads) so {interface}.{method} stays a stable identity for
    /// the binding checks.
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

    // The internal store ports: the I*Store interfaces in the runtime assembly. The provider binding and
    // completeness gates both read this, so a newly declared port is picked up by both with no registry
    // to update.
    internal static List<Type> StoreInterfaces() =>
        [
            .. typeof(ActaServiceCollectionExtensions)
                .Assembly.GetTypes()
                .Where(t => t.IsInterface && t.Namespace == "Acta.Runtime" && StoreInterfaceName.IsMatch(t.Name)),
        ];

    internal static IEnumerable<MethodInfo> DeclaredMethods(Type store) => store.GetMethods().Where(m => !m.IsSpecialName);

    [GeneratedRegex(@"^I\w+Store$", RegexOptions.Compiled)]
    private static partial Regex MyRegex();
}
