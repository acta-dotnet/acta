using System.Reflection;
using System.Text.RegularExpressions;
using Acta.Generators;
using Acta.Tests.Docs;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Acta.Tests.Generators;

/// <summary>
/// Every ACTA01xx descriptor's help link lands on its own section of docs/diagnostics.md, and the doc
/// describes exactly the ids the generator can report. The anchors are read from the tree, never fetched.
/// </summary>
public sealed partial class DiagnosticHelpLinkTests
{
    private static readonly IReadOnlyList<DiagnosticDescriptor> Descriptors =
    [
        .. typeof(ManifestDiagnostics)
            .GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(field => field.FieldType == typeof(DiagnosticDescriptor))
            .Select(field => (DiagnosticDescriptor)field.GetValue(null)!),
    ];

    [Fact]
    public void Every_descriptor_links_to_its_own_section_and_the_doc_has_no_other()
    {
        var doc = File.ReadAllText(Path.Combine(DocSampleExtraction.RepoRoot, "docs", "diagnostics.md"));
        var anchors = AnchorRegex().Matches(doc).Select(m => m.Groups[1].Value).Order(StringComparer.Ordinal);

        Assert.NotEmpty(Descriptors);
        foreach (var descriptor in Descriptors)
        {
            var link = HelpLinkRegex().Match(descriptor.HelpLinkUri);
            Assert.True(
                link.Success,
                $"{descriptor.Id} links to '{descriptor.HelpLinkUri}', not the diagnostics doc at a revision or main."
            );
            Assert.Equal(descriptor.Id.ToLowerInvariant(), link.Groups["anchor"].Value);
        }
        Assert.Equal(Descriptors.Select(d => d.Id.ToLowerInvariant()).Order(StringComparer.Ordinal), anchors);
    }

    [Theory]
    [InlineData("1.0.0+cd39c5c3d45f3406eaf4bc2c711b7db7b3ecf167", "cd39c5c3d45f3406eaf4bc2c711b7db7b3ecf167")]
    [InlineData("1.0.0-rc.7+cd39c5c3d45f3406eaf4bc2c711b7db7b3ecf167", "cd39c5c3d45f3406eaf4bc2c711b7db7b3ecf167")]
    [InlineData("1.0.0", "main")]
    [InlineData("1.0.0+cd39c5c", "main")]
    [InlineData(null, "main")]
    public void The_help_base_pins_the_build_revision_and_falls_back_to_main(string? informationalVersion, string segment) =>
        Assert.Equal(
            $"https://github.com/acta-dotnet/acta/blob/{segment}/docs/diagnostics.md#",
            ManifestDiagnostics.HelpBaseFor(informationalVersion)
        );

    [GeneratedRegex("""<a id="(acta\d{4})"></a>""")]
    private static partial Regex AnchorRegex();

    [GeneratedRegex(@"^https://github\.com/acta-dotnet/acta/blob/([0-9a-f]{40}|main)/docs/diagnostics\.md#(?<anchor>acta\d{4})$")]
    private static partial Regex HelpLinkRegex();
}
