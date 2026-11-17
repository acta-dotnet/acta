using Acta.Emit.Features.Learn;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Emit;

// The how-tos are written from the concept programs. These drive the emitter against a throwaway root that holds one
// rung, so each rule of the markup is a fact of its own; the last test holds the real repository to its own pages.
public sealed class LearnEmitterTests : IDisposable
{
    private const string Rung = "concepts/000-basics/001-first";
    private const string Page = "site/learn/001-first.html";

    private static readonly string Program = Normalized(
        """
        // How to greet someone
        // One line says what the program shows.
        using Acta;

        var jobs = host.Services.GetRequiredService<IJobs>();

        // learn: Enqueue the greeting
        // The call writes the job as a row.
        await jobs.EnqueueAsync(new Greet("Ada"));

        // learn: end
        Console.WriteLine("enqueued");
        """
    );

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"acta-learn-{Guid.NewGuid():N}");

    public LearnEmitterTests()
    {
        Write($"{Rung}/first.cs", Program);
        Write("concepts/concept-runs.json", """{ "001-first": ["enqueued"] }""");
        Write(
            LearnLibrary.SectionsFile,
            """
            {
              "sections": [{ "key": "start", "title": "Start jobs", "blurb": "Ways to start work.", "howTos": ["001-first"] }],
              "tour": ["001-first"],
              "lookAlikes": [],
              "searchWords": {}
            }
            """
        );
        Write(
            LearnPages.Tutorial,
            """
            <style>
              /* Self-hosted fonts */
            </style>
              <a class="skip-link" href="#content">Skip</a>
              <main id="content">
                <footer class="site-footer">
                </footer>
              </main>
            """
        );
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void A_marked_step_is_a_step_of_the_page_with_its_lines_emphasised()
    {
        var emitted = LearnEmitter.Emit(_root);

        var page = emitted[Page];
        Assert.Contains("<h1>How to greet someone</h1>", page, StringComparison.Ordinal);
        Assert.Contains("<span class=\"step-n\">1</span>Enqueue the greeting</h2>", page, StringComparison.Ordinal);
        Assert.Contains("<p>The call writes the job as a row.</p>", page, StringComparison.Ordinal);
        Assert.Contains("<span class=\"hl\"><span class=\"k\">await</span> jobs.EnqueueAsync(", page, StringComparison.Ordinal);

        var twin = emitted["site/learn/001-first.md"];
        Assert.Contains("## 1. Enqueue the greeting", twin, StringComparison.Ordinal);
        Assert.Contains("Its output must contain:\n\n```text\nenqueued\n```", twin, StringComparison.Ordinal);
        Assert.Contains("https://useacta.net/learn/001-first</loc>", emitted["site/learn/sitemap.xml"], StringComparison.Ordinal);
        Assert.Contains(
            "| 001 | [first](../../concepts/000-basics/001-first/) |",
            emitted["docs/reference/concept-ladder.md"],
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void A_rule_and_a_question_in_the_readme_reach_the_page()
    {
        Write(
            $"{Rung}/README.md",
            """
            # How to greet someone

            ## Remember

            - The job is a row before anything runs it.

            ## Check yourself

            **Where is the job after the enqueue?**

            - In memory
            - In a row

            <details><summary>Answer</summary>

            In a row. The enqueue stores it.

            </details>
            """
        );

        var page = LearnEmitter.Emit(_root)[Page];

        Assert.Contains("<li>The job is a row before anything runs it.</li>", page, StringComparison.Ordinal);
        Assert.Contains("<div class=\"check\" data-answer=\"1\">", page, StringComparison.Ordinal);
        Assert.Contains("<b>In a row.</b> The enqueue stores it.", page, StringComparison.Ordinal);
    }

    [Fact]
    public void A_step_left_open_stops_the_emit()
    {
        Write($"{Rung}/first.cs", Program.Replace("// learn: end\n", "", StringComparison.Ordinal));

        var stop = Assert.Throws<LearnException>(() => LearnEmitter.Emit(_root));

        Assert.Contains("the step \"Enqueue the greeting\" is never closed", stop.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_program_that_marks_no_step_stops_the_emit()
    {
        Write($"{Rung}/first.cs", "// How to greet someone\n// One line.\nConsole.WriteLine(\"enqueued\");\n");

        var stop = Assert.Throws<LearnException>(() => LearnEmitter.Emit(_root));

        Assert.Contains("no \"// learn:\" step", stop.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_program_that_names_no_task_stops_the_emit()
    {
        Write($"{Rung}/first.cs", Program.Replace("// How to greet someone\n", "", StringComparison.Ordinal));

        var stop = Assert.Throws<LearnException>(() => LearnEmitter.Emit(_root));

        Assert.Contains("no file opens with a \"// How to\" comment", stop.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rung_in_no_section_stops_the_emit()
    {
        Write("concepts/000-basics/002-second/second.cs", Program);
        Write("concepts/concept-runs.json", """{ "001-first": ["enqueued"], "002-second": ["enqueued"] }""");

        var stop = Assert.Throws<LearnException>(() => LearnEmitter.Emit(_root));

        Assert.Contains("concepts/000-basics/002-second is in no section", stop.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_step_in_a_long_type_shows_the_member_it_stands_on_and_no_long_neighbour()
    {
        var filler = string.Join('\n', Enumerable.Range(1, 20).Select(n => $"        Console.WriteLine({n});"));
        Write(
            $"{Rung}/first.cs",
            $$"""
            // How to greet someone
            // One line says what the program shows.
            public sealed class Jobs
            {
                // learn: Give the job a retry budget
                [Job(
                    "greet",
                    MaxAttempts = 3
                )]
                // learn: end
                public void Greet(Greet input)
                {
                    Console.WriteLine("enqueued");
                }

                [Job("other")]
                public void Other(Other input)
                {
            {{filler}}
                }
            }
            """
        );

        var twin = LearnEmitter.Emit(_root)["site/learn/001-first.md"];

        Assert.Contains("    MaxAttempts = 3\n)]\npublic void Greet(Greet input)\n{", twin, StringComparison.Ordinal);
        Assert.DoesNotContain("public void Other", twin, StringComparison.Ordinal);
        Assert.DoesNotContain("public sealed class Jobs", twin, StringComparison.Ordinal);
    }

    [Fact]
    public void A_page_no_program_stands_behind_is_a_stray()
    {
        Write("site/learn/009-gone.html", "<html></html>");

        var strays = LearnEmitter.Strays(_root, LearnEmitter.Emit(_root));

        Assert.Equal(["site/learn/009-gone.html"], strays);
    }

    [Fact]
    public void The_repositorys_how_tos_are_the_ones_its_programs_emit()
    {
        var root = IntegrationConfig.FindRepoRoot();

        var emitted = LearnEmitter.Emit(root);

        Assert.NotEmpty(emitted);
        var stale = LearnEmitter.Stale(root, emitted).Concat(LearnEmitter.Strays(root, emitted)).ToList();
        Assert.True(
            stale.Count == 0,
            "The how-tos no longer match the concept programs. Regenerate them: dotnet run --project tools/Acta.Emit -- docs\n  "
                + string.Join("\n  ", stale.Take(12))
        );
    }

    private static string Normalized(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private void Write(string relative, string text)
    {
        var path = LearnLibrary.PathOf(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Normalized(text));
    }
}
