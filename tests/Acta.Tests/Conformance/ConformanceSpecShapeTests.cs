using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
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
        var candidates = typeof(IConformanceFixture).Assembly.GetTypes().Where(HarnessReflection.IsCandidateContractSpec).ToList();
        var failures = new List<string>();

        foreach (var spec in candidates)
        {
            var a = HarnessReflection.ConformanceSpec(spec);
            if (a is null)
            {
                continue;
            }

            void Fail(string why) => failures.Add($"{spec.Name}: {why}");

            foreach (var (field, text) in new[] { ("Contract", a.Contract), ("Arrange", a.Arrange), ("Act", a.Act), ("Assert", a.Assert) })
            {
                if (text.Length > SentenceMaxChars)
                {
                    Fail($"{field} > {SentenceMaxChars} chars ({text.Length}).");
                }
                if (text.Contains('\n') || text.Contains(';'))
                {
                    Fail($"{field} must be a single sentence (no newline/semicolon).");
                }
                if (text.Length > 0 && !text.TrimEnd().EndsWith('.'))
                {
                    Fail($"{field} must end with '.'.");
                }
            }
            if (a.Title.Length > TitleMaxChars)
            {
                Fail($"Title > {TitleMaxChars} chars.");
            }

            var guarantees = HarnessReflection.Guarantees(spec);
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
