using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using static Acta.Emit.Features.Learn.CSharpColour;

namespace Acta.Emit.Features.Learn;

/// <summary>
/// Writes the pages of the Learn section. The header, the footer and the font rules come from the tutorial page,
/// <c>site/learn/first-project.html</c>, which is kept by hand: a change to the site's navigation there reaches every
/// how-to on the next run.
/// </summary>
internal sealed partial class LearnPages
{
    public const string Tutorial = "site/learn/first-project.html";
    private const string Origin = "https://useacta.net";
    private const string Repository = LearnText.Repository;

    private readonly LearnLibrary _library;
    private readonly LearnText _text;
    private readonly string _fontStyle;
    private readonly string _preloads;
    private readonly string _chrome;
    private readonly string _footer;

    public LearnPages(LearnLibrary library, string repoRoot)
    {
        _library = library;
        _text = new LearnText(library);
        var tutorial = LearnLibrary.Read(repoRoot, Tutorial);
        var fonts = FontStyle().Match(tutorial);
        _fontStyle = fonts.Success ? fonts.Value : throw new LearnException($"{Tutorial} no longer has its font rules");
        _preloads = string.Join("\n  ", Preload().Matches(tutorial).Select(m => m.Value));
        _chrome = Between(tutorial, "  <a class=\"skip-link\"", "  <main id=\"content\">", "header");
        _footer = Between(tutorial, "    <footer class=\"site-footer\">", "    </footer>", "footer") + "    </footer>";
    }

    private static string Between(string tutorial, string from, string to, string what)
    {
        var start = tutorial.IndexOf(from, StringComparison.Ordinal);
        var end = start < 0 ? -1 : tutorial.IndexOf(to, start, StringComparison.Ordinal);
        return start >= 0 && end >= 0 ? tutorial[start..end] : throw new LearnException($"{Tutorial} no longer has its {what}");
    }

    // ---- shared page pieces ----

    private string Page(
        string title,
        string description,
        string url,
        string md,
        string body,
        string attrs,
        IEnumerable<string> structuredData
    )
    {
        var structured = string.Concat(
            structuredData.Select(json =>
                $"\n  <script type=\"application/ld+json\">{json.Replace("<", "\\u003c", StringComparison.Ordinal)}</script>"
            )
        );
        return $"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1" />
              <title>{Esc(title)}</title>
              <meta name="description" content="{Esc(description)}" />
              <link rel="canonical" href="{Origin}{url}" />
              <meta name="theme-color" content="#0B0E15" />
              <meta property="og:type" content="article" />
              <meta property="og:site_name" content="Acta for .NET" />
              <meta property="og:title" content="{Esc(title)}" />
              <meta property="og:description" content="{Esc(description)}" />
              <meta property="og:url" content="{Origin}{url}" />
              <meta property="og:image" content="{Origin}/assets/social-card.png" />
              <meta property="og:image:width" content="1200" />
              <meta property="og:image:height" content="630" />
              <meta property="og:image:alt" content="Acta: durable execution for .NET. Postgres, SQL Server, SQLite." />
              <meta name="twitter:card" content="summary_large_image" />{structured}
              <link rel="icon" href="/favicon.svg" type="image/svg+xml" />
              <link rel="alternate" type="text/plain" href="/llms.txt" title="Acta for coding agents" />
              <link rel="alternate" type="text/markdown" href="{md}" title="This page as Markdown, for coding agents" />
              {_preloads}
              {_fontStyle}
              <link rel="stylesheet" href="/assets/site.css" />
              <link rel="stylesheet" href="/assets/learn.css" />
              <script src="/assets/theme.js"></script>
            </head>
            <body>
            {_chrome}
              <main id="content"{attrs}>
            {body}
            {_footer}
              </main>
              <p class="sr-only" id="copy-status" aria-live="polite"></p>
              <script src="/assets/learn.js"></script>
            </body>
            </html>

            """;
    }

    private static string ReaderSwitch(string md) =>
        $"""
            <div class="reader" role="group" aria-label="Read this page as">
                    <span class="reader-label">Read as</span>
                    <button type="button" data-reader="person" aria-pressed="true">a person</button>
                    <button type="button" data-reader="agent" aria-pressed="false" data-md="{md}">an agent</button>
                  </div>
            """;

    private static string AgentFace(string md) =>
        $"""
            <article class="agent-face" hidden>
                    <figure class="listing agent"><figcaption><span class="file">{md}</span><span class="note" data-md-size></span><a class="copy" href="{md}">Open</a><button class="copy" type="button" data-copy-target="#md-body">Copy</button></figcaption><pre><code id="md-body">Loading the Markdown.</code></pre></figure>
                    <p class="hint">This is the page an agent receives: the same how-to as plain Markdown, at this page's address with <code>.md</code> added.</p>
                  </article>
            """;

    /// <summary>A listing whose emphasised lines carry a band.</summary>
    private static string RowsHtml(IReadOnlyList<ShownRow> rows)
    {
        var indent = Math.Min(99, rows.Where(row => row.Source.Trim().Length > 0).Select(Indent).DefaultIfEmpty(99).Min());
        var lines = Colour(string.Join('\n', rows.Select(row => row.Source[Math.Min(indent, row.Source.Length)..]))).Split('\n');
        return string.Join(
            '\n',
            lines.Select(
                (line, n) =>
                    n < rows.Count && rows[n].Emphasised && Count(line, "<span") == Count(line, "</span>")
                        ? $"<span class=\"hl\">{(line.Length > 0 ? line : " ")}</span>"
                        : line
            )
        );
    }

    private static int Indent(ShownRow row) => row.Source.Length - row.Source.TrimStart(' ').Length;

    private static int Count(string text, string part)
    {
        var count = 0;
        for (
            var at = text.IndexOf(part, StringComparison.Ordinal);
            at >= 0;
            at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal)
        )
        {
            count++;
        }
        return count;
    }

    private static (string Key, string Label, string Text) StatusOf(HowTo entry) =>
        entry.Prints is not null
            ? (
                "checked",
                "Checked every release",
                "Before each release, Acta Verify runs this program and checks that its output contains these lines."
            )
        : entry.Skip is not null ? ("proven", "Proven by other suites", $"The release check does not run this program. {entry.Skip}")
        : ("checked", "Runs as a test", "This program is a test project: it passes or fails with the test suite.");

    private static string Marks(HowTo entry) => entry.Lab ? "<span class=\"mark lab\">Lab</span>" : "";

    // ---- one how-to: the person's page ----

    private List<(string Title, string Html)> Steps(HowTo entry)
    {
        var list = entry
            .Flow.Select(step =>
                (
                    step.Title,
                    (step.Prose.Length > 0 ? $"<p>{_text.Inline(step.Prose, entry.Dir)}</p>\n" : "")
                        + _text.Listing(step.File, step.Part ? "excerpt" : "", RowsHtml(step.Rows))
                )
            )
            .ToList();
        var status = StatusOf(entry);
        var run =
            $"<p>From a clone of <a href=\"{Repository}\">the Acta repository</a>, with the .NET 10 SDK:</p>\n"
            + _text.Listing(null, "Terminal", Esc(entry.Command));
        if (entry.Prints is not null)
        {
            run += _text.Listing(null, "Its output must contain", Esc(string.Join('\n', entry.Prints)), output: true);
        }
        run += $"<p class=\"proof {status.Key}\"><b>{Esc(status.Label)}.</b> {Esc(status.Text)}</p>\n";
        if (entry.Rules.Count == 0)
        {
            run +=
                $"<p class=\"hint\">This page shows the code. The rules it relies on, starting with the handler running again from its first line, are in <a href=\"{Repository}/blob/main/docs/developer-guide.md#handler-contract\">the handler contract</a>.</p>\n";
        }
        list.Add((entry.Prints is not null ? "Run it and check the output" : "Run it", run));
        return list;
    }

    private string PersonFace(HowTo entry)
    {
        var html = new StringBuilder();
        var steps = Steps(entry);
        for (var n = 0; n < steps.Count; n++)
        {
            html.Append(
                CultureInfo.InvariantCulture,
                $"<div class=\"step\"><h2><span class=\"step-n\">{n + 1}</span>{Esc(steps[n].Title)}</h2>\n{steps[n].Html}</div>\n"
            );
        }
        if (entry.Rules.Count > 0)
        {
            var rules = string.Concat(entry.Rules.Select(rule => $"<li>{_text.Inline(rule, entry.Dir)}</li>"));
            html.Append(
                CultureInfo.InvariantCulture,
                $"<div class=\"step plain-step\"><h2>Remember</h2><ul class=\"rules\">{rules}</ul></div>\n"
            );
        }
        if (entry.Checks.Count > 0)
        {
            html.Append("<div class=\"step plain-step\"><h2>Check yourself</h2>");
            foreach (var check in entry.Checks)
            {
                var options = string.Concat(
                    check.Options.Select(option => $"<li><button type=\"button\">{_text.Inline(option, entry.Dir)}</button></li>")
                );
                html.Append(
                    CultureInfo.InvariantCulture,
                    $"<div class=\"check\" data-answer=\"{check.Answer}\"><p class=\"check-q\">{_text.Inline(check.Question, entry.Dir)}</p><ol class=\"check-options\">{options}</ol><details class=\"check-why\"><summary>Show the answer</summary><p><b>{_text.Inline(check.Options[check.Answer], entry.Dir)}.</b> {_text.Inline(check.Why, entry.Dir)}</p></details></div>"
                );
            }
            html.Append("</div>\n");
        }

        // A rung's notes stay on the page, folded, so the how-to above them stays short.
        if (entry.Notes.Length > 0)
        {
            var summary = entry.Lab ? LabPrefix().Replace(entry.Heading ?? "", "The lab behind it: ") : "Notes on the program";
            var lab = entry.Lab
                ? "<p>This program is an Engineering Lab: beside the how-to it prints the rows that show what happened, and its notes weigh the design against the alternatives.</p>\n"
                : "";
            html.Append(
                CultureInfo.InvariantCulture,
                $"<details class=\"step lab-notes\"><summary>{_text.Inline(summary)}</summary>\n{lab}{_text.Blocks(entry.Notes, entry.Dir, 3)}</details>\n"
            );
        }

        var first = _library.ById["001"];
        var helpers =
            entry.Helpers.Count > 0
                ? $"<p class=\"hint\">{_text.Inline(string.Join(" and ", entry.Helpers))} {(entry.Helpers.Count > 1 ? "are helpers" : "is a helper")} of the Acta repository, not of the packages. In your own app, register the database with <code>UseSqlite</code>, <code>UsePostgres</code> or <code>UseSqlServer</code>, as in <a href=\"{first.Url}\">{Esc(first.Title.ToLowerInvariant())}</a>.</p>\n"
                : "";
        var files = string.Join(
            '\n',
            entry.Files.Select(file =>
            {
                var id = _text.NextListingId();
                var rows = file.Rows.Select(row => new ShownRow(row.Source, row.Kind == RowKind.Code && row.Step >= 0)).ToList();
                return $"<details class=\"source-file\"><summary><span class=\"file-name\">{Esc(file.Name)}</span><span class=\"file-size\">{file.LineCount} lines</span></summary><figure class=\"listing\"><figcaption><span class=\"file\">{Esc(file.Name)}</span><button class=\"copy\" type=\"button\" data-copy-target=\"#{id}\">Copy</button></figcaption><pre><code id=\"{id}\">{RowsHtml(rows)}</code></pre></figure></details>";
            })
        );
        html.Append(
            CultureInfo.InvariantCulture,
            $"<div class=\"step plain-step\"><h2>The whole program</h2>\n<p>Every file of <a href=\"{Repository}/tree/main/{entry.Dir}\"><code>{Esc(entry.Dir)}</code></a>, as it is compiled.</p>\n{helpers}{files}</div>\n"
        );
        if (entry.Onward is { } onward)
        {
            html.Append(
                CultureInfo.InvariantCulture,
                $"<div class=\"step plain-step\"><h2>Where to go next</h2><ul class=\"next-links\"><li><a href=\"{onward.Url}\">{Esc(onward.Title)}</a><span>{Esc(onward.Section.Title)}</span></li></ul></div>\n"
            );
        }
        return html.ToString();
    }

    public string HowToPage(HowTo entry)
    {
        _text.StartPage();
        var status = StatusOf(entry);
        var section = entry.Section;
        var rail = string.Join(
            "\n            ",
            section.Entries.Select(e =>
                $"<li{(e == entry ? " aria-current=\"page\"" : "")}><a href=\"{e.Url}\" data-id=\"{e.Id}\"><span class=\"rail-n\">{e.Step}</span>{Esc(e.ShortTitle)}</a></li>"
            )
        );
        var previous = entry.Before is { } before ? $"<a href=\"{before.Url}\" rel=\"prev\">Previous</a>" : "<span></span>";
        var next = entry.After is { } after ? $"<a href=\"{after.Url}\" rel=\"next\">Next</a>" : "";
        var sectionName = Esc(section.Title.ToLowerInvariant());
        var body = $"""
                <header class="wrap page-head howto-head">
                  <p class="record"><a href="/learn">learn acta</a><span aria-hidden="true">/</span><a href="/learn#s-{section.Key}">{sectionName}</a></p>
                  <h1>{Esc(entry.Title)}</h1>
                  <p class="lede">{_text.Inline(entry.Summary + ".", entry.Dir)}</p>
                  <p class="howto-context">A how-to for Acta, durable execution for .NET. <a href="/start">New to Acta? Run your first durable job in five minutes.</a></p>
                  <div class="howto-meta">
                    <span class="status {status.Key}">{Esc(status.Label)}</span>{Marks(entry)}
                    <a class="program" href="{Repository}/tree/main/{entry.Dir}"><code>{Esc(entry.Dir)}</code></a>
                    {ReaderSwitch(entry.Url + ".md")}
                  </div>
                </header>
                <div class="wrap howto-body">
                  <article class="howto person-face">
            {PersonFace(entry)}      </article>
                  {AgentFace(entry.Url + ".md")}
                  <aside class="howto-rail" aria-label="This section">
                    <p class="rail-head"><a href="/learn#s-{section.Key}">{Esc(section.Title)}</a></p>
                    <ol>
                        {rail}
                    </ol>
                    <button class="button done-toggle" type="button" data-done-toggle="{entry.Id}" aria-pressed="false">Mark as done</button>
                    <p class="rail-turn">{previous}{next}</p>
                  </aside>
                </div>
                <section class="wrap newcomer" aria-labelledby="newcomer-title">
                  <h2 id="newcomer-title">New to Acta?</h2>
                  <p class="lede">Acta is durable execution for .NET: workflows and jobs that survive restarts, as a library inside your application with its durable state in your SQL database.</p>
                  <div class="actions">
                    <a class="button primary" href="/start">Run your first durable job in five minutes</a>
                    <a class="button" href="/learn">See every how-to</a>
                    <a class="button" href="/capabilities">Everything Acta does</a>
                  </div>
                </section>
            """;

        // What a search result shows: the task, then what Acta is, then what the page gives.
        var description = $"{entry.Title} with Acta, durable execution for .NET. {LearnText.Plain(entry.Summary)}.";
        var about =
            $"{{\"@type\":\"SoftwareSourceCode\",\"name\":\"Acta\",\"programmingLanguage\":\"C#\",\"codeRepository\":{Json(Repository)}}}";
        string[] structuredData =
        [
            $"{{\"@context\":\"https://schema.org\",\"@type\":\"TechArticle\",\"headline\":{Json(entry.Title)},\"description\":{Json(description)},\"url\":{Json(Origin + entry.Url)},\"inLanguage\":\"en\",\"articleSection\":{Json(section.Title)},\"isPartOf\":{{\"@type\":\"CollectionPage\",\"name\":\"Learn Acta\",\"url\":{Json(Origin + "/learn")}}},\"about\":{about}}}",
            $"{{\"@context\":\"https://schema.org\",\"@type\":\"BreadcrumbList\",\"itemListElement\":[{{\"@type\":\"ListItem\",\"position\":1,\"name\":\"Learn Acta\",\"item\":{Json(Origin + "/learn")}}},{{\"@type\":\"ListItem\",\"position\":2,\"name\":{Json(section.Title)},\"item\":{Json($"{Origin}/learn#s-{section.Key}")}}},{{\"@type\":\"ListItem\",\"position\":3,\"name\":{Json(entry.Title)},\"item\":{Json(Origin + entry.Url)}}}]}}",
        ];
        return Page(
            $"{entry.Title} · Acta, durable execution for .NET",
            description,
            entry.Url,
            entry.Url + ".md",
            body,
            $" data-howto=\"{entry.Id}\"",
            structuredData
        );
    }

    /// <summary>A string as a JSON value, written the way the browser's own serializer writes it.</summary>
    private static string Json(string value)
    {
        var json = new StringBuilder("\"");
        foreach (var c in value)
        {
            json.Append(
                c switch
                {
                    '"' => "\\\"",
                    '\\' => "\\\\",
                    '\b' => "\\b",
                    '\f' => "\\f",
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    < ' ' => $"\\u{(int)c:x4}",
                    _ => c.ToString(),
                }
            );
        }
        return json.Append('"').ToString();
    }

    [GeneratedRegex(@"<style>\s*/\* Self-hosted[\s\S]*?</style>")]
    private static partial Regex FontStyle();

    [GeneratedRegex(@"<link rel=""preload""[^>]*>")]
    private static partial Regex Preload();

    [GeneratedRegex("^Engineering Lab: ")]
    private static partial Regex LabPrefix();
}
