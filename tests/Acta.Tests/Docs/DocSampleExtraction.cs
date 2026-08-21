using System.Net;
using System.Text.RegularExpressions;

namespace Acta.Tests.Docs;

/// <summary>
/// The one parser for code samples embedded in shipped prose: fenced <c>csharp</c> blocks in
/// Markdown-ish documents and <c>&lt;pre&gt;&lt;code&gt;</c> blocks in the site's HTML, with entities
/// decoded and line endings normalized so a CRLF checkout compares equal to an LF one.
/// </summary>
/// <remarks>
/// Shared by the drift gates in this project and by tests/DocSamples/Extractor, which materializes
/// the same blocks as real projects and compiles them against the candidate packages. Both halves
/// read the docs through this file so a sample can never pass one gate under different rules.
/// </remarks>
public static partial class DocSampleExtraction
{
    /// <summary>The documents that must all carry the identical first-run program.</summary>
    public static readonly string[] FirstRunDocuments = ["llms.txt", "README.md", "docs/quickstart.md", "site/start.html"];

    /// <summary>Opening line of the first-run program; the marker that identifies it in any document.</summary>
    public const string FirstRunMarker = "using Shipping;";

    /// <summary>Repository root, located by walking up from the running assembly to Acta.slnx.</summary>
    public static string RepoRoot { get; } = ResolveRepoRoot();

    /// <summary>
    /// Reads a document by repo-relative path and returns its code blocks in document order.
    /// Markdown yields only <c>csharp</c>-fenced blocks; the site's HTML has no language marker on a
    /// <c>&lt;pre&gt;&lt;code&gt;</c> block, so it yields every one of them, shell commands included.
    /// Callers select the sample they mean by content, which is language-agnostic either way.
    /// </summary>
    public static IReadOnlyList<string> CodeBlocks(string relativeDocument)
    {
        var text = Normalize(File.ReadAllText(Path.Combine(RepoRoot, relativeDocument.Replace('/', Path.DirectorySeparatorChar))));
        return relativeDocument.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ? HtmlCodeBlocks(text) : CSharpFences(text);
    }

    /// <summary>
    /// The first-run program as the document publishes it. Exactly one block must open with
    /// <see cref="FirstRunMarker"/>; zero or several means the document changed shape.
    /// </summary>
    public static string FirstRunProgram(string relativeDocument)
    {
        var matches = CodeBlocks(relativeDocument).Where(b => b.StartsWith(FirstRunMarker, StringComparison.Ordinal)).ToList();
        return matches.Count == 1
            ? matches[0]
            : throw new InvalidOperationException(
                $"{relativeDocument}: expected exactly one code block opening with '{FirstRunMarker}', found {matches.Count}."
            );
    }

    /// <summary>The single code block containing <paramref name="marker"/>, for samples selected by content.</summary>
    public static string BlockContaining(string relativeDocument, string marker)
    {
        var matches = CodeBlocks(relativeDocument).Where(b => b.Contains(marker, StringComparison.Ordinal)).ToList();
        return matches.Count == 1
            ? matches[0]
            : throw new InvalidOperationException(
                $"{relativeDocument}: expected exactly one code block containing '{marker}', found {matches.Count}."
            );
    }

    /// <summary>
    /// Code blocks whose first line is a <c>// File: path</c> header, paired with that path. This is
    /// how a multi-file sample names the files it must be split into to compile as one project.
    /// </summary>
    public static IReadOnlyList<(string File, string Code)> FileHeaderedBlocks(string relativeDocument)
    {
        const string header = "// File: ";
        var blocks = new List<(string, string)>();
        foreach (var block in CodeBlocks(relativeDocument))
        {
            var firstLine = block.Split('\n')[0];
            if (firstLine.StartsWith(header, StringComparison.Ordinal))
            {
                blocks.Add((firstLine[header.Length..].Trim(), block));
            }
        }
        return blocks;
    }

    /// <summary>Every job name declared by a <c>[Job("...")]</c> attribute in the given code.</summary>
    public static IReadOnlyList<string> JobNames(string code) => JobAttributeRegex().Matches(code).Select(m => m.Groups[1].Value).ToList();

    /// <summary>Line-ending normalization plus trailing-blank trim: what "byte-identical" means here.</summary>
    public static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');

    private static IReadOnlyList<string> CSharpFences(string text)
    {
        var blocks = new List<string>();
        var lines = text.Split('\n');
        var open = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (open < 0)
            {
                if (line == "```csharp")
                {
                    open = i + 1;
                }
                continue;
            }
            if (line == "```")
            {
                blocks.Add(Normalize(string.Join('\n', lines[open..i])));
                open = -1;
            }
        }

        return open < 0 ? blocks : throw new InvalidOperationException("Unterminated ```csharp fence.");
    }

    private static IReadOnlyList<string> HtmlCodeBlocks(string text) =>
        PreCodeRegex().Matches(text).Select(m => Normalize(WebUtility.HtmlDecode(m.Groups[1].Value))).ToList();

    private static string ResolveRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Acta.slnx")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "DocSampleExtraction could not locate Acta.slnx marking the repo root from " + AppContext.BaseDirectory
        );
    }

    [GeneratedRegex(@"<pre><code>(.*?)</code></pre>", RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex PreCodeRegex();

    [GeneratedRegex(@"\[Job\(\s*""([^""]*)""", RegexOptions.Compiled)]
    private static partial Regex JobAttributeRegex();
}
