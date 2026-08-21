using Xunit;

namespace Acta.Tests.Docs;

/// <summary>
/// Drift gates over the code samples a newcomer meets first. These run without packages: they pin
/// the corpus shape, the one-program-behind-every-door invariant, the two copies of llms.txt, and
/// the job-name rule. Compiling the same samples from the packed bytes is tests/DocSamples/run.ps1.
/// </summary>
public sealed class DocSampleDriftTests
{
    private const string Llms = "llms.txt";
    private const string Readme = "README.md";
    private const string Quickstart = "docs/quickstart.md";
    private const string StartPage = "site/start.html";

    private const string Registration =
        "tests/DocSamples/run.ps1 compiles the samples this count covers. Adding one means giving it a "
        + "project group in that runner (and, in docs/quickstart.md, a '// File: <path>' first line) "
        + "before the count moves.";

    [Fact]
    public void Every_front_door_document_yields_the_samples_the_harness_expects()
    {
        // docs/quickstart.md is deliberately absent: Every_quickstart_sample_is_compiled_or_explicitly_elided
        // covers it by meaning rather than by count, which survives editing that a number does not.
        Assert.True(DocSampleExtraction.CodeBlocks(Llms).Count == 2, $"llms.txt must carry 2 C# samples. {Registration}");
        Assert.True(DocSampleExtraction.CodeBlocks(Readme).Count == 1, $"README.md must carry 1 C# sample. {Registration}");

        // The HTML door has no language marker on its blocks, so this count covers the bash block too.
        var startBlocks = DocSampleExtraction.CodeBlocks(StartPage);
        Assert.True(startBlocks.Count == 2, $"site/start.html must carry 2 code blocks. {Registration}");

        // The HTML door is extracted through entity decoding; a raw read would leave &lt; in the source.
        Assert.DoesNotContain(startBlocks, block => block.Contains("&lt;", StringComparison.Ordinal));

        Assert.Contains("class WebhookJobs", DocSampleExtraction.BlockContaining(Llms, "class WebhookJobs"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_first_run_program_is_byte_identical_in_every_door()
    {
        // No document is the designated truth: the doors are grouped by the program they publish, so a
        // drifting llms.txt reads as one group against three rather than as three drifting documents.
        var variants = DocSampleExtraction
            .FirstRunDocuments.GroupBy(DocSampleExtraction.FirstRunProgram, StringComparer.Ordinal)
            .Select(group => group.OrderBy(doc => doc, StringComparer.Ordinal).ToList())
            .OrderByDescending(group => group.Count)
            .ToList();

        Assert.True(
            variants.Count == 1,
            "The first-run program must be the same program in every door (line endings normalized), but the "
                + $"documents publish {variants.Count} different programs. Each line is one program's documents, "
                + "largest group first:\n  "
                + string.Join("\n  ", variants.Select(group => string.Join(", ", group)))
        );
    }

    [Fact]
    public void The_site_copy_of_llms_txt_matches_the_repository_root_copy()
    {
        // Raw bytes, not normalized text: the site copy is a copy, so a byte-order mark or a line-ending
        // change is drift too. This is the one comparison in the file that is literal.
        var root = File.ReadAllBytes(Path.Combine(DocSampleExtraction.RepoRoot, "llms.txt"));
        var site = File.ReadAllBytes(Path.Combine(DocSampleExtraction.RepoRoot, "site", "llms.txt"));

        Assert.True(
            root.AsSpan().SequenceEqual(site),
            $"site/llms.txt has drifted from the repository root llms.txt ({root.Length} bytes vs {site.Length}); "
                + "the published copy must be a byte-for-byte copy."
        );
    }

    [Fact]
    public void Every_quickstart_sample_is_compiled_or_explicitly_elided()
    {
        // The compile harness builds the first-run program and the file-headered group. Anything else
        // must be an ellipsis fragment; a new whole sample has to declare its file and join the build.
        var firstRun = DocSampleExtraction.FirstRunProgram(Quickstart);
        var headered = DocSampleExtraction.FileHeaderedBlocks(Quickstart).Select(b => b.Code).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(3, headered.Count);

        var uncovered = DocSampleExtraction
            .CodeBlocks(Quickstart)
            .Where(block => block != firstRun && !headered.Contains(block) && !block.Contains("...", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            uncovered.Count == 0,
            "docs/quickstart.md carries a whole C# sample the compile harness does not build. Give it a "
                + "'// File: <path>' first line so it joins the group, or elide it with '...':\n\n"
                + string.Join("\n\n", uncovered)
        );
    }

    [Fact]
    public void Every_job_name_in_a_sample_is_a_legal_user_job_name()
    {
        var failures = new List<string>();
        foreach (var document in DocSampleExtraction.FirstRunDocuments)
        {
            foreach (var block in DocSampleExtraction.CodeBlocks(document))
            {
                foreach (var name in DocSampleExtraction.JobNames(block))
                {
                    try
                    {
                        IdentifierSyntax.ValidateUserKebab(name, "jobName", IdentifierSyntax.ExtendedMaxLength);
                    }
                    catch (ArgumentException ex)
                    {
                        failures.Add($"{document}: [Job(\"{name}\")] {ex.Message}");
                    }
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "A documented sample declares a job name the runtime would reject at registration:\n  " + string.Join("\n  ", failures)
        );
    }

    [Fact]
    public void The_documented_samples_actually_declare_job_names()
    {
        // Guards the gate above against a silent pass: an extraction that stopped finding attributes
        // would validate an empty set and stay green forever.
        var names = DocSampleExtraction
            .FirstRunDocuments.SelectMany(DocSampleExtraction.CodeBlocks)
            .SelectMany(DocSampleExtraction.JobNames)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["deliver-webhook", "send-welcome-email", "ship-order"], names);
    }
}
