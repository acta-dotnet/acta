using System.Reflection;
using Xunit;

namespace Acta.Tests.Conformance;

/// <summary>
/// Provider completeness gate: every store port surfaced as a property on <see cref="IActaStore"/>
/// has exactly one implementation in each relational provider assembly. Grows automatically as the
/// composite gains properties; a provider that misses a feature slice fails here before any runtime
/// wiring is exercised.
/// </summary>
public sealed class ProviderStoreCompletenessTests
{
    private static readonly string[] ProviderAssemblies = ["Acta.Postgres", "Acta.SqlServer", "Acta.Sqlite"];

    [Fact]
    public void Every_provider_implements_every_store_port()
    {
        var ports = typeof(IActaStore).GetProperties().Select(p => p.PropertyType).ToList();
        Assert.NotEmpty(ports);

        // Every store port has exactly one shared implementation in Acta.Relational and no leftover
        // per-provider implementation: the consolidation is complete.
        var relational = Assembly.Load("Acta.Relational");
        var providerTypes = ProviderAssemblies.SelectMany(name => Assembly.Load(name).GetTypes()).ToList();
        var missing = new List<string>();
        foreach (var port in ports)
        {
            var shared = relational.GetTypes().Count(t => t.IsClass && !t.IsAbstract && port.IsAssignableFrom(t));
            if (shared != 1)
            {
                missing.Add($"{port.Name}: {shared} shared implementations in Acta.Relational (expected exactly 1)");
            }

            var leftover = providerTypes.Count(t => t.IsClass && !t.IsAbstract && port.IsAssignableFrom(t));
            if (leftover != 0)
            {
                missing.Add($"{port.Name}: {leftover} leftover per-provider implementations (expected 0 after consolidation)");
            }
        }

        Assert.True(missing.Count == 0, "Store ports without exactly one provider implementation:\n" + string.Join("\n", missing));
    }
}
