using Xunit;

namespace Acta.Tests.Docs;

/// <summary>
/// The first-job guide shows files that live as concept rungs, so they compile and their test runs. Every code block
/// the guide opens with a <c>// File:</c> line must be that rung file, byte for byte after line-ending normalization
/// and without the rung's how-to markup; a guide that drifts from its rung is a guide that no longer compiles.
/// </summary>
public sealed class FirstJobGuideTests
{
    private const string Guide = "docs/first-job.md";

    private static readonly IReadOnlyDictionary<string, string> RungFiles = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Carrier.cs"] = "concepts/000-fundamentals/026-first-job/Carrier.cs",
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
                WithoutHowToMarkup(DocSampleExtraction.Normalize(file)) == DocSampleExtraction.Normalize(blocks[name]),
                $"{Guide}'s {name} block differs from {path}; the guide shows the rung's file verbatim."
            );
        }
    }

    /// <summary>
    /// The file as the guide shows it: without the <c>// How to</c> comment that names the rung's task and without the
    /// <c>// learn:</c> lines that mark its steps. A mark the formatter set between two blank lines takes one of them
    /// with it.
    /// </summary>
    private static string WithoutHowToMarkup(string file)
    {
        var lines = file.Split('\n').ToList();
        var title = lines.FindIndex(line => line.StartsWith("// How to ", StringComparison.Ordinal));
        if (title >= 0)
        {
            var end = title + 1;
            while (end < lines.Count && lines[end].StartsWith("//", StringComparison.Ordinal))
            {
                end++;
            }
            lines.RemoveRange(title, end - title);
        }

        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (!lines[i].TrimStart().StartsWith("// learn: ", StringComparison.Ordinal))
            {
                continue;
            }
            lines.RemoveAt(i);
            if (i > 0 && i < lines.Count && lines[i - 1].Length == 0 && lines[i].Length == 0)
            {
                lines.RemoveAt(i);
            }
        }
        return string.Join('\n', lines);
    }
}
