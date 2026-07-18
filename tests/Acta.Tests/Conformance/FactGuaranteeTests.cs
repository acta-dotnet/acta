using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance;

/// <summary>
/// Completeness gate: every public [Fact]/[Theory] declared directly on a conformance spec carries a
/// DisplayName, since the DisplayName is the human-readable guarantee rendered into the contracts doc.
/// </summary>
public sealed class FactGuaranteeTests
{
    /// <summary>
    /// Asserts every executable fact on a candidate spec names the guarantee it proves.
    /// </summary>
    [Fact]
    public void Every_fact_declares_a_display_name()
    {
        var candidates = typeof(IConformanceFixture).Assembly.GetTypes().Where(HarnessReflection.IsCandidateContractSpec).ToList();
        var failures = new List<string>();

        foreach (var spec in candidates)
        {
            foreach (var method in HarnessReflection.FactMethods(spec))
            {
                if (string.IsNullOrWhiteSpace(HarnessReflection.DisplayName(method)))
                {
                    failures.Add($"{spec.Name}.{method.Name}: [Fact]/[Theory] is missing a DisplayName guarantee.");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
