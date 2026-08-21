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

    [Fact]
    public void Every_front_door_document_yields_the_samples_the_harness_expects()
    {
        Assert.Equal(2, DocSampleExtraction.CodeBlocks(Llms).Count);
        Assert.Single(DocSampleExtraction.CodeBlocks(Readme));
        Assert.Equal(5, DocSampleExtraction.CodeBlocks(Quickstart).Count);

        // The HTML door is extracted through entity decoding; a raw read would leave &lt; in the source.
        var startBlocks = DocSampleExtraction.CodeBlocks(StartPage);
        Assert.Equal(2, startBlocks.Count);
        Assert.DoesNotContain(startBlocks, block => block.Contains("&lt;", StringComparison.Ordinal));

        Assert.Contains("class WebhookJobs", DocSampleExtraction.BlockContaining(Llms, "class WebhookJobs"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_first_run_program_is_byte_identical_in_every_door()
    {
        var canonical = DocSampleExtraction.FirstRunProgram(Llms);
        var drifting = DocSampleExtraction.FirstRunDocuments.Where(doc => DocSampleExtraction.FirstRunProgram(doc) != canonical).ToList();

        Assert.True(
            drifting.Count == 0,
            "The first-run program must be the same program in every door (line endings normalized). "
                + $"These documents differ from {Llms}:\n  "
                + string.Join("\n  ", drifting)
        );
    }

    [Fact]
    public void The_site_copy_of_llms_txt_matches_the_repository_root_copy()
    {
        var root = DocSampleExtraction.Normalize(File.ReadAllText(Path.Combine(DocSampleExtraction.RepoRoot, "llms.txt")));
        var site = DocSampleExtraction.Normalize(File.ReadAllText(Path.Combine(DocSampleExtraction.RepoRoot, "site", "llms.txt")));

        Assert.True(root == site, "site/llms.txt has drifted from the repository root llms.txt; the published copy must be a copy.");
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
