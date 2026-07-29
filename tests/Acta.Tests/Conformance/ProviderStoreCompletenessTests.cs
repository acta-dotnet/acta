using System.Reflection;
using Xunit;

namespace Acta.Tests.Conformance;

/// <summary>
/// Provider completeness gate: every internal store port has exactly one implementation in
/// Acta.Relational and none left in a provider assembly. Ports are discovered by the same reflection
/// convention the coverage gate uses (<c>I*Store</c> under <c>Acta.Features.*</c> /
/// <c>Acta.Services.*</c>), so a new port is covered the moment it is declared - no registry to
/// remember to update. A provider that misses a feature slice fails here before any runtime wiring
/// is exercised.
/// </summary>
public sealed class ProviderStoreCompletenessTests
{
    private static readonly string[] ProviderAssemblies = ["Acta.Postgres", "Acta.SqlServer", "Acta.Sqlite"];

    [Fact]
    public void Every_provider_implements_every_store_port()
    {
        var ports = StoreContractCoverageTests.StoreInterfaces();
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
