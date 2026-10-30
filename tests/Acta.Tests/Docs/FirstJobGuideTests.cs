using Xunit;

namespace Acta.Tests.Docs;

/// <summary>
/// The first-job guide shows files that live as concept rungs, so they compile and their test runs. Every code block
/// the guide opens with a <c>// File:</c> line must be that rung file, byte for byte after line-ending normalization;
/// a guide that drifts from its rung is a guide that no longer compiles.
/// </summary>
public sealed class FirstJobGuideTests
{
    private const string Guide = "docs/first-job.md";

    private static readonly IReadOnlyDictionary<string, string> RungFiles = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Jobs.cs"] = "concepts/000-fundamentals/026-first-job/Jobs.cs",
        ["LabelPrinter.cs"] = "concepts/000-fundamentals/026-first-job/LabelPrinter.cs",
        ["Program.cs"] = "concepts/000-fundamentals/026-first-job/Program.cs",
        ["ShippingTests.cs"] = "concepts/800-testing/803-first-job-tests/ShippingTests.cs",
    };

    [Fact]
    public void Every_file_block_of_the_guide_is_its_rung_file()
    {
        var blocks = DocSampleExtraction
            .CodeBlocks(Guide)
            .Where(block => block.StartsWith("// File: ", StringComparison.Ordinal))
            .ToDictionary(block => block[9..block.IndexOf('\n')].Trim(), StringComparer.Ordinal);

        Assert.Equal(RungFiles.Keys.Order(StringComparer.Ordinal), blocks.Keys.Order(StringComparer.Ordinal));
        foreach (var (name, path) in RungFiles)
        {
            var file = File.ReadAllText(Path.Combine(DocSampleExtraction.RepoRoot, path.Replace('/', Path.DirectorySeparatorChar)));
            Assert.True(
                DocSampleExtraction.Normalize(file) == DocSampleExtraction.Normalize(blocks[name]),
                $"{Guide}'s {name} block differs from {path}; the guide shows the rung's file verbatim."
            );
        }
    }
}
