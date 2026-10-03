using Xunit;

namespace Acta.Tests.Conformance;

/// <summary>
/// The HUMAN+++ lint: every contract reads as a thin, absorbable unit.
/// </summary>
public sealed class ConformanceSpecShapeTests
{
    private const int SentenceMaxChars = 160;
    private const int TitleMaxChars = 80;
    private const int GuaranteesMax = 16;

    /// <summary>
    /// Asserts every contract attribute stays within the thin-shape caps.
    /// </summary>
    [Fact]
    public void Every_contract_is_thin_and_absorbable()
    {
        var candidates = ConformanceSpecSources.All;
        var failures = new List<string>();

        foreach (var spec in candidates)
        {
            var a = spec.Contract;
            if (a is null)
            {
                continue;
            }

            void Fail(string why) => failures.Add($"{spec.Name}: {why}");

            if (a.Contract.Length > SentenceMaxChars)
            {
                Fail($"Contract > {SentenceMaxChars} chars ({a.Contract.Length}).");
            }
            if (a.Contract.Contains('\n') || a.Contract.Contains(';'))
            {
                Fail("Contract must be a single sentence (no newline/semicolon).");
            }
            if (a.Contract.Length > 0 && !a.Contract.TrimEnd().EndsWith('.'))
            {
                Fail("Contract must end with '.'.");
            }
            if (a.Title.Length > TitleMaxChars)
            {
                Fail($"Title > {TitleMaxChars} chars.");
            }

            var guarantees = spec.Guarantees;
            if (guarantees.Count is 0 or > GuaranteesMax)
            {
                Fail($"Guarantees must be 1..{GuaranteesMax} (was {guarantees.Count}).");
            }
            if (guarantees.Distinct().Count() != guarantees.Count)
            {
                Fail("Duplicate Guarantees (Fact DisplayName).");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
