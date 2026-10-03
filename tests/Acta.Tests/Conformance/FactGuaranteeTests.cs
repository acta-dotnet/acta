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
        var candidates = ConformanceSpecSources.All;
        var failures = new List<string>();

        foreach (var spec in candidates)
        {
            foreach (var fact in spec.Facts)
            {
                if (string.IsNullOrWhiteSpace(fact.DisplayName))
                {
                    failures.Add($"{spec.Name}.{fact.Method}: [Fact]/[Theory] is missing a DisplayName guarantee.");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
