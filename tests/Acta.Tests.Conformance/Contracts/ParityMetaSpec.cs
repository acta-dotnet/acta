using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Contracts;

/// <summary>
/// Parity gate (per provider): every candidate contract spec has exactly one concrete subclass bound
/// to this provider's fixture in the executing assembly. Runs without a DB connection.
/// </summary>
/// <typeparam name="TFixture">The provider fixture this binding checks.</typeparam>
public abstract class ParityMetaSpec<TFixture> : IConformanceMetaSpec<TFixture>
    where TFixture : IConformanceFixture, new()
{
    /// <summary>
    /// Asserts this provider binds every contract spec exactly once.
    /// </summary>
    [Fact]
    public void Provider_binds_every_contract_spec()
    {
        var conformanceAssembly = typeof(IConformanceFixture).Assembly;
        var providerAssembly = GetType().Assembly;

        var specs = conformanceAssembly.GetTypes().Where(HarnessReflection.IsCandidateContractSpec).ToList();
        var bindingsByBase = providerAssembly
            .GetTypes()
            .Where(t => t is { IsAbstract: false, IsGenericTypeDefinition: false })
            .Select(t => HarnessReflection.ClosedSpecBaseBoundTo(t, typeof(TFixture)))
            .Where(b => b is not null)
            .GroupBy(b => b!)
            .ToDictionary(g => g.Key, g => g.Count());

        var failures = new List<string>();
        foreach (var spec in specs)
        {
            bindingsByBase.TryGetValue(spec, out var count);
            if (count != 1)
            {
                failures.Add($"{spec.Name}: expected exactly 1 {typeof(TFixture).Name} binding, found {count}.");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
