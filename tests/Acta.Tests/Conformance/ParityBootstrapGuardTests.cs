using System.Text.RegularExpressions;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance;

/// <summary>
/// Closes the parity bootstrap gap with NO test-project references: scans provider source for the
/// ParityMetaSpec binding so a provider that forgets it fails the build.
/// </summary>
public sealed partial class ParityBootstrapGuardTests
{
    /// <summary>
    /// Asserts every provider test project declaring an IConformanceFixture also binds ParityMetaSpec for it.
    /// </summary>
    [Fact]
    public void Every_provider_with_a_fixture_binds_parity()
    {
        var testsDir = Path.Combine(IntegrationConfig.FindRepoRoot(), "tests");
        var failures = new List<string>();

        foreach (var projectDir in Directory.GetDirectories(testsDir, "Acta.Tests.*"))
        {
            var files = Directory.GetFiles(projectDir, "*.cs", SearchOption.AllDirectories);
            // Match IConformanceFixture anywhere in the base list (handles `: SomeBase, IConformanceFixture`),
            // not only immediately after the colon - otherwise a multi-interface fixture slips the guard.
            var fixtures = files.SelectMany(f => MyRegex().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value)).Distinct().ToList();

            foreach (var fixture in fixtures)
            {
                var bound = files.Any(f => File.ReadAllText(f).Contains($"ParityMetaSpec<{fixture}>", StringComparison.Ordinal));
                if (!bound)
                {
                    failures.Add($"{Path.GetFileName(projectDir)}: declares {fixture} but has no ParityMetaSpec<{fixture}> binding.");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [GeneratedRegex(@"class\s+(\w+ConformanceFixture)\s*:[^{}]*\bIConformanceFixture\b")]
    private static partial Regex MyRegex();
}
