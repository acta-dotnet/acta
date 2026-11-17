using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Acta.Emit.Features.Docs;

namespace Acta.Emit.Features.Learn;

/// <summary>
/// What an agent reads: every how-to as plain Markdown at its page's address with <c>.md</c> added, its whole program
/// in a second file, the index of them all, the section's sitemap, and the table of how-tos in the repository's
/// reference docs.
/// </summary>
internal sealed partial class LearnPages
{
    private static string Fence(string text, string lang = "csharp") => $"```{lang}\n{text}\n```\n";

    public const string Table = "docs/reference/concept-ladder.md";

    private static string PlainRows(IEnumerable<ShownRow> rows) => string.Join('\n', LearnText.Dedent(rows.Select(row => row.Source)));

    private string NotInPackages(HowTo entry)
    {
        var several = entry.Helpers.Count > 1;
        var first = _library.ById["001"];
        return $"{string.Join(" and ", entry.Helpers)} {(several ? "are helpers" : "is a helper")} of the Acta repository (`support/`), used so every program runs on any of the three databases{(several ? " and prints its rows" : "")}. Do not copy {(several ? "them" : "it")} into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [{first.Title.ToLowerInvariant()}]({first.Url}.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.";
    }

    public string HowToMarkdown(HowTo entry)
    {
        var status = StatusOf(entry);
        string Linked(string text) => _text.MarkdownLinks(text, entry.Dir);
        var md = new StringBuilder($"# {entry.Title}\n\n{Linked(entry.Summary)}.\n\n");
        md.Append(
            CultureInfo.InvariantCulture,
            $"- Section: {entry.Section.Title}, step {entry.Step}\n- Program: `{entry.Dir}` in {Repository} (one compiled project)\n"
        );
        md.Append(CultureInfo.InvariantCulture, $"- Status: {status.Label}. {status.Text}\n");
        md.Append(
            CultureInfo.InvariantCulture,
            $"- Rules: [/llms.txt](/llms.txt) is the contract every handler follows{(entry.Rules.Count > 0 ? "; \"Remember\" below carries the rules that bite this task" : ", starting with the handler running again from its first line; read it before you copy code from here")}.\n"
        );
        md.Append(
            CultureInfo.InvariantCulture,
            $"- Release: the one /llms.txt names at its top.\n- Page for people: {entry.Url}\n- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.\n\n"
        );

        var steps = entry
            .Flow.Select(step =>
            {
                var key = step.Rows.Where(row => row.Emphasised).ToList();
                var where = $"`{step.File}`{(step.Part ? " (excerpt)" : "")}:\n\n";
                var lead =
                    key.Count > 0 && key.Count < step.Rows.Count ? $"The lines that matter:\n\n{Fence(PlainRows(key))}\nIn {where}" : where;
                return (step.Title, Text: (step.Prose.Length > 0 ? Linked(step.Prose) + "\n\n" : "") + lead + Fence(PlainRows(step.Rows)));
            })
            .ToList();
        var run = "From a clone of the repository, with the .NET 10 SDK:\n\n" + Fence(entry.Command, "bash") + "\n";
        if (entry.Prints is not null)
        {
            run += "Its output must contain:\n\n" + Fence(string.Join('\n', entry.Prints), "text") + "\n";
        }
        steps.Add((entry.Prints is not null ? "Run it and check the output" : "Run it", run + $"{status.Label}. {status.Text}\n"));
        for (var n = 0; n < steps.Count; n++)
        {
            md.Append(CultureInfo.InvariantCulture, $"## {n + 1}. {steps[n].Title}\n\n{steps[n].Text}\n");
        }

        if (entry.Rules.Count > 0)
        {
            md.Append(
                CultureInfo.InvariantCulture,
                $"## Remember\n\n{string.Join('\n', entry.Rules.Select(rule => "- " + Linked(rule)))}\n\n"
            );
        }
        if (entry.Checks.Count > 0)
        {
            var questions = entry.Checks.Select(
                (check, n) =>
                    $"{n + 1}. {check.Question}\n{string.Join('\n', check.Options.Select((option, k) => $"   - {"abcdef"[k]}) {option}"))}\n   - Answer: {"abcdef"[check.Answer]}. {Linked(check.Why)}"
            );
            md.Append(CultureInfo.InvariantCulture, $"## Check yourself\n\n{string.Join('\n', questions)}\n\n");
        }
        if (entry.Helpers.Count > 0)
        {
            md.Append(CultureInfo.InvariantCulture, $"## Not in the packages\n\n{NotInPackages(entry)}\n\n");
        }
        if (entry.Onward is { } onward)
        {
            md.Append(CultureInfo.InvariantCulture, $"## Next\n\n- [{onward.Title}]({onward.Url}.md)\n\n");
        }

        // The how-to ends here. What a reader rarely needs sits in a second file, so the first one stays small.
        var lines = entry.Files.Sum(file => file.Rows.Count);
        md.Append(
            CultureInfo.InvariantCulture,
            $"## More\n\n- [The whole program{(entry.Notes.Length > 0 ? " and its notes" : "")}]({entry.Url}.program.md): every file as compiled, {lines} lines{(entry.Lab ? ", and the notes on the design, the rows to inspect, and how to break it" : "")}.\n"
        );
        return BlankRuns().Replace(md.ToString(), "\n\n");
    }

    /// <summary>The second file of a how-to: its whole program and the notes its README keeps.</summary>
    public string ProgramMarkdown(HowTo entry)
    {
        var md = new StringBuilder(
            $"# The whole program: {entry.Title[7..]}\n\nThe how-to is [{entry.Url}.md]({entry.Url}.md). This file holds every source file of `{entry.Dir}` as it is compiled{(entry.Notes.Length > 0 ? ", and the notes kept beside the code" : "")}.\n\n"
        );
        if (entry.Helpers.Count > 0)
        {
            md.Append(CultureInfo.InvariantCulture, $"## Not in the packages\n\n{NotInPackages(entry)}\n\n");
        }
        md.Append(
            CultureInfo.InvariantCulture,
            $"## The whole program\n\n{string.Join('\n', entry.Files.Select(file => $"### {file.Name}\n\n{Fence(file.Text)}"))}\n"
        );
        if (entry.Notes.Length > 0)
        {
            md.Append(
                CultureInfo.InvariantCulture,
                $"## Notes\n\n{_text.MarkdownLinks(SecondLevelHeading().Replace(entry.Notes, "### "), entry.Dir)}\n"
            );
        }
        return BlankRuns().Replace(md.ToString(), "\n\n");
    }

    public string IndexMarkdown()
    {
        var md = new StringBuilder(
            "# Learn Acta: the how-tos, for coding agents\n\n> Each how-to teaches one task with a small compiled program from the Acta repository: the handler, the call that starts it, the command that runs it, and the lines it must print. Every link below is plain Markdown.\n\n"
        );
        md.Append(
            "Read /llms.txt first. It is the contract: the rules every handler follows. This index answers \"show me the smallest working example of the task\".\n\n"
        );
        md.Append(
            "## How to use a how-to\n\n- The numbered steps are what you write. The code is lifted from the program named on the page, so it compiles as shown.\n- \"Its output must contain\" lists the lines the program prints when the task works. The release check reads the same lines.\n- `UseLocalDatabase` and `ConceptLab` appear in the programs' host code. They are helpers of the repository, not of the packages: in an application write `UseSqlite`, `UsePostgres` or `UseSqlServer`.\n- Rows printed by a lab come from SQL views, the operator's contract. Application code reads the same facts through `IJobs` and `IActaOperations`.\n- A how-to is short on purpose. Its whole program, and the notes kept beside it, are in a second file named at its end, `<name>.program.md`; open that only when the steps are not enough.\n- The how-tos describe the release /llms.txt names at its top.\n- Addresses are relative to this file's site. The page for people is the same address without `.md`.\n\n"
        );
        md.Append(
            CultureInfo.InvariantCulture,
            $"## The short tour\n\n{string.Join('\n', _library.Tour.Select((e, n) => $"{n + 1}. [{e.Title}]({e.Url}.md)"))}\n\n"
        );
        var lookAlikes = _library.LookAlikes.Select(pair =>
            $"- {pair.Ask}:\n{string.Join('\n', pair.Pick.Select(p => $"  - {p.When}: [{p.Entry.Title}]({p.Entry.Url}.md)"))}"
        );
        md.Append(CultureInfo.InvariantCulture, $"## Which one when\n\n{string.Join('\n', lookAlikes)}\n\n");
        foreach (var section in _library.Sections)
        {
            md.Append(CultureInfo.InvariantCulture, $"## {section.Title}\n\n{section.Blurb}\n\n");
            md.Append(
                string.Join('\n', section.Entries.Select(e => $"- [{e.Title}]({e.Url}.md): {_text.MarkdownLinks(e.Summary, e.Dir)}"))
                    + "\n\n"
            );
        }
        return md.ToString();
    }

    /// <summary>Search engines get every how-to. The rest of the site, the tutorial included, is in the hand-kept sitemap.</summary>
    public string SiteMap() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n"
        + string.Concat(_library.Ordered.Select(entry => $"  <url>\n    <loc>{Origin}{entry.Url}</loc>\n  </url>\n"))
        + "</urlset>\n";

    /// <summary>The table in the repository's reference docs: every how-to by number, with its page.</summary>
    public string TableDocument()
    {
        var md = new StringBuilder(
            "# The concept ladder\n\nEvery rung is a small program in [`concepts/`](../../concepts/) that teaches one task, numbered in bands so the ladder can grow without renumbering. Each rung is also a how-to at [useacta.net/learn](https://useacta.net/learn), written from the same program: its title and its one-line summary are the program's opening comment.\n\nThis file is generated. Regenerate it with `dotnet run --project tools/Acta.Emit -- docs`.\n"
        );
        foreach (var band in _library.Bands)
        {
            md.Append(
                CultureInfo.InvariantCulture,
                $"\n## {band}\n\n| Rung | Program | How to | What it shows |\n| --- | --- | --- | --- |\n"
            );
            foreach (var e in _library.ByFolder.Values.Where(e => e.Band == band).OrderBy(e => e.Folder, StringComparer.Ordinal))
            {
                md.Append(
                    CultureInfo.InvariantCulture,
                    $"| {e.Id} | [{e.Name}](../../{e.Dir}/) | [{e.ShortTitle}]({Origin}{e.Url}) | {(e.Lab ? "**Engineering Lab.** " : "")}{MarkdownEscape.Cell(e.Summary)} |\n"
                );
            }
        }
        return md.ToString();
    }

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankRuns();

    [GeneratedRegex("^## ", RegexOptions.Multiline)]
    private static partial Regex SecondLevelHeading();
}
