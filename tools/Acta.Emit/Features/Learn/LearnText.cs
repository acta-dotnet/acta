using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using static Acta.Emit.Features.Learn.CSharpColour;

namespace Acta.Emit.Features.Learn;

/// <summary>
/// The Markdown the how-tos are written in, read into the site's HTML: a program's comments and a README's rules take
/// <c>`code`</c>, <c>**bold**</c> and links; a README's notes also take headings, lists, tables and fenced code. A
/// link to another how-to's folder becomes a link to its page.
/// </summary>
internal sealed partial class LearnText(LearnLibrary library)
{
    public const string Repository = "https://github.com/acta-dotnet/acta";

    /// <summary>The lines without the indent they all share.</summary>
    public static List<string> Dedent(IEnumerable<string> lines)
    {
        var all = lines.ToList();
        var indent = all.Where(line => line.Trim().Length > 0)
            .Select(line => line.Length - line.TrimStart(' ').Length)
            .DefaultIfEmpty(0)
            .Min();
        return all.Select(line => line[Math.Min(indent, line.Length)..]).ToList();
    }

    /// <summary>A block of code with its caption and its copy button, which copies the block it stands on.</summary>
    public static string Listing(string? file, string? note, string bodyHtml, bool output = false)
    {
        var caption =
            (file is { Length: > 0 } ? $"<span class=\"file\">{Esc(file)}</span>" : "")
            + (note is { Length: > 0 } ? $"<span class=\"note\">{Esc(note)}</span>" : "");
        return $"<figure class=\"listing{(output ? " output" : "")}\"><figcaption>{caption}<button class=\"copy\" type=\"button\">Copy</button></figcaption><pre><code>{bodyHtml}</code></pre></figure>\n";
    }

    /// <summary>Where a link leads from a page: another how-to, the site itself, or the file on GitHub.</summary>
    public string LinkTarget(string href, string baseDir, bool markdown)
    {
        if (Absolute().IsMatch(href))
        {
            return href;
        }
        var parts = href.Split('#');
        var file = parts[0];
        var hash = parts.Length > 1 ? parts[1] : "";
        var resolved = Normalize(baseDir.Length > 0 ? $"{baseDir}/{file}" : file);
        var howTo = HowToPath().Match(resolved);
        if (howTo.Success && library.ById.TryGetValue(howTo.Groups[1].Value, out var entry))
        {
            return entry.Url + (markdown ? ".md" : "");
        }
        var trimmed = resolved.TrimEnd('/');
        var name = trimmed[(trimmed.LastIndexOf('/') + 1)..];
        var isFolder = file.EndsWith('/') || name.LastIndexOf('.') <= 0;
        return $"{Repository}/{(isFolder ? "tree" : "blob")}/main/{trimmed}{(hash.Length > 0 ? "#" + hash : "")}";
    }

    private static string Normalize(string path)
    {
        var kept = new List<string>();
        foreach (var segment in path.Split('/'))
        {
            if (segment is "" or ".")
            {
                continue;
            }
            if (segment == ".." && kept.Count > 0 && kept[^1] != "..")
            {
                kept.RemoveAt(kept.Count - 1);
                continue;
            }
            kept.Add(segment);
        }
        var joined = string.Join('/', kept);
        if (joined.Length == 0)
        {
            return ".";
        }
        return path.EndsWith('/') ? joined + "/" : joined;
    }

    /// <summary>One line of prose as HTML: links first, so a label that is a code span keeps its own markup.</summary>
    public string Inline(string text, string baseDir = "")
    {
        var held = new List<string>();
        string Hold(string html)
        {
            held.Add(html);
            return $"\0{held.Count - 1}\0";
        }

        var s = Link()
            .Replace(text, m =>
                Hold($"<a href=\"{Esc(LinkTarget(m.Groups[2].Value, baseDir, markdown: false))}\">{Inline(m.Groups[1].Value, baseDir)}</a>")
            );
        s = CodeSpan().Replace(s, m => Hold($"<code>{Esc(m.Groups[1].Value)}</code>"));
        s = Bold().Replace(Esc(s), "<b>$1</b>");
        return Held().Replace(s, m => held[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)]);
    }

    public static string Plain(string text) =>
        PlainLink().Replace(CodeSpan().Replace(text, "$1"), "$1").Replace("**", "", StringComparison.Ordinal);

    /// <summary>Markdown kept as Markdown, with its links pointed where a reader of the twin can follow them.</summary>
    public string MarkdownLinks(string text, string baseDir) =>
        MarkdownLink().Replace(text, m => $"[{m.Groups[1].Value}]({LinkTarget(m.Groups[2].Value, baseDir, markdown: true)})");

    /// <summary>A README's notes as HTML, their headings one level down, under the page's own.</summary>
    public string Blocks(string markdown, string baseDir)
    {
        var lines = markdown.Split('\n');
        var html = new StringBuilder();
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            if (line.Trim().Length == 0)
            {
                i++;
                continue;
            }
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                var lang = line[3..].Trim();
                var body = new List<string>();
                for (i++; i < lines.Length && !lines[i].StartsWith("```", StringComparison.Ordinal); i++)
                {
                    body.Add(lines[i]);
                }
                i++;
                var code = string.Join('\n', body);
                var note = lang switch
                {
                    "csharp" => "C#",
                    "bash" => "Terminal",
                    "text" or "" => "Text",
                    _ => lang,
                };
                html.Append(Listing(null, note, lang == "csharp" ? Colour(code) : Esc(code)));
                continue;
            }
            var heading = Heading().Match(line);
            if (heading.Success)
            {
                var level = heading.Groups[1].Length + 1;
                html.Append(CultureInfo.InvariantCulture, $"<h{level}>{Inline(heading.Groups[2].Value, baseDir)}</h{level}>\n");
                i++;
                continue;
            }
            if (line.StartsWith("| ", StringComparison.Ordinal))
            {
                var rows = new List<string[]>();
                for (; i < lines.Length && lines[i].StartsWith('|'); i++)
                {
                    rows.Add(TableEdges().Replace(lines[i], "").Split('|').Select(cell => cell.Trim()).ToArray());
                }
                html.Append("<div class=\"table-scroll\"><table class=\"data-table compare\"><thead><tr>");
                html.Append(string.Concat(rows[0].Select(cell => $"<th>{Inline(cell, baseDir)}</th>")));
                html.Append("</tr></thead><tbody>");
                foreach (var row in rows.Skip(2))
                {
                    html.Append("<tr>");
                    html.Append(
                        string.Concat(
                            row.Select((cell, n) => n == 0 ? $"<th>{Inline(cell, baseDir)}</th>" : $"<td>{Inline(cell, baseDir)}</td>")
                        )
                    );
                    html.Append("</tr>");
                }
                html.Append("</tbody></table></div>\n");
                continue;
            }
            var bullet = ListItem().Match(line);
            if (bullet.Success)
            {
                var ordered = bullet.Groups[1].Value.Any(char.IsAsciiDigit);
                var items = new List<string>();
                while (i < lines.Length)
                {
                    var item = ListItem().Match(lines[i]);
                    if (!item.Success)
                    {
                        break;
                    }
                    var text = item.Groups[2].Value;
                    for (i++; i < lines.Length && lines[i].Trim().Length > 0 && char.IsWhiteSpace(lines[i][0]); i++)
                    {
                        text += " " + lines[i].Trim();
                    }
                    items.Add(text);
                }
                var tag = ordered ? "ol" : "ul";
                html.Append(
                    CultureInfo.InvariantCulture,
                    $"<{tag} class=\"plain\">{string.Concat(items.Select(item => $"<li>{Inline(item, baseDir)}</li>"))}</{tag}>\n"
                );
                continue;
            }
            var paragraph = line;
            for (i++; i < lines.Length && lines[i].Trim().Length > 0 && !BlockStart().IsMatch(lines[i]); i++)
            {
                paragraph += " " + lines[i].Trim();
            }
            html.Append(CultureInfo.InvariantCulture, $"<p>{Inline(paragraph, baseDir)}</p>\n");
        }
        return html.ToString();
    }

    [GeneratedRegex(@"^(https?:|mailto:|#|/)")]
    private static partial Regex Absolute();

    [GeneratedRegex("^" + LearnLibrary.Root + @"/[^/]+/(\d{3})-[a-z0-9-]+/?$")]
    private static partial Regex HowToPath();

    [GeneratedRegex(@"\[((?:`[^`]*`|[^\]`])+)\]\(([^)\s]+)\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)\s]+)\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex PlainLink();

    [GeneratedRegex("`([^`]+)`")]
    private static partial Regex CodeSpan();

    [GeneratedRegex(@"\*\*([^*]+)\*\*")]
    private static partial Regex Bold();

    [GeneratedRegex(@"\x00(\d+)\x00")]
    private static partial Regex Held();

    [GeneratedRegex(@"^(#{1,4}) (.+)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\||\|\s*$")]
    private static partial Regex TableEdges();

    [GeneratedRegex(@"^([-*]|\d+\.) (.*)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^(#{1,4} |```|\| |[-*] |\d+\. )")]
    private static partial Regex BlockStart();
}
