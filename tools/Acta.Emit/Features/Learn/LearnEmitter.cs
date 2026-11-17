using System.Text.RegularExpressions;

namespace Acta.Emit.Features.Learn;

/// <summary>
/// The Learn section as files: one page, one Markdown twin and one program file per how-to, the library's front page,
/// the index for agents, the <c>/learn/</c> entries of the sitemap, and the ladder table in the reference docs. Nothing
/// here is edited by hand: a change to a program, a README or <c>concepts/learn.json</c> changes what is emitted.
/// </summary>
internal static partial class LearnEmitter
{
    private const string Pages = "site/learn";
    private const string SiteMap = "site/sitemap.xml";

    /// <summary>The tutorial under <c>site/learn/</c> is written by hand, and lends the how-tos its header and footer.</summary>
    private static readonly string HandKept = Path.GetFileName(LearnPages.Tutorial);

    /// <summary>
    /// Every emitted file by its repo-relative path. A root with no <c>concepts/learn.json</c> has no Learn section,
    /// which is what the throwaway roots of the schema commands' tests are.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Emit(string repoRoot)
    {
        var emitted = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!LearnLibrary.Exists(repoRoot, LearnLibrary.SectionsFile))
        {
            return emitted;
        }

        var library = LearnLibrary.Load(repoRoot);
        var pages = new LearnPages(library, repoRoot);
        foreach (var entry in library.Ordered)
        {
            emitted[$"{Pages}/{entry.Folder}.html"] = pages.HowToPage(entry);
            emitted[$"{Pages}/{entry.Folder}.md"] = pages.HowToMarkdown(entry);
            emitted[$"{Pages}/{entry.Folder}.program.md"] = pages.ProgramMarkdown(entry);
        }
        emitted["site/learn.html"] = pages.Hub();
        emitted[$"{Pages}/index.md"] = pages.IndexMarkdown();
        emitted["docs/reference/concept-ladder.md"] = pages.LadderDocument();

        // Search engines get every how-to in the sitemap. The entries under /learn/ are rewritten on every run; the
        // rest of the file is kept by hand.
        var siteMap = LearnEntry().Replace(LearnLibrary.Read(repoRoot, SiteMap), "");
        if (!siteMap.Contains("</urlset>", StringComparison.Ordinal))
        {
            throw new LearnException($"{SiteMap} has no </urlset>");
        }
        var urls = new[] { "/learn/" + Path.GetFileNameWithoutExtension(HandKept) }.Concat(library.Ordered.Select(entry => entry.Url));
        emitted[SiteMap] = siteMap.Replace(
            "</urlset>",
            string.Concat(urls.Select(url => $"  <url>\n    <loc>https://useacta.net{url}</loc>\n  </url>\n")) + "</urlset>",
            StringComparison.Ordinal
        );

        foreach (var file in emitted.Keys.ToList())
        {
            emitted[file] = emitted[file].Replace("\r\n", "\n", StringComparison.Ordinal);
        }

        // Every link into the section must land on a page or a Markdown twin that is emitted.
        foreach (var (file, text) in emitted.Where(pair => pair.Key.StartsWith(Pages, StringComparison.Ordinal)))
        {
            foreach (Match link in LearnLink().Matches(text))
            {
                var target = "site" + Fragment().Replace(link.Groups[1].Value, "");
                if (!emitted.ContainsKey(target) && !emitted.ContainsKey(target + ".html") && target + ".html" != LearnPages.Tutorial)
                {
                    throw new LearnException($"{file} links to {link.Groups[1].Value}, which does not exist");
                }
            }
        }
        return emitted;
    }

    /// <summary>Files under <c>site/learn/</c> that no program stands behind any more.</summary>
    public static IReadOnlyList<string> Strays(string repoRoot, IReadOnlyDictionary<string, string> emitted)
    {
        var directory = Path.Combine(repoRoot, "site", "learn");
        if (emitted.Count == 0 || !Directory.Exists(directory))
        {
            return [];
        }
        return Directory
            .EnumerateFiles(directory)
            .Select(path => Path.GetFileName(path)!)
            .Where(name => name != HandKept && !emitted.ContainsKey($"{Pages}/{name}"))
            .Select(name => $"{Pages}/{name}")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Writes the section and removes its strays; answers how many files it wrote, or -1 when a rung stops it.</summary>
    public static int Write(string repoRoot)
    {
        IReadOnlyDictionary<string, string> emitted;
        try
        {
            emitted = Emit(repoRoot);
        }
        catch (LearnException stop)
        {
            Console.Error.WriteLine($"  learn: {stop.Message}");
            return -1;
        }

        foreach (var stray in Strays(repoRoot, emitted))
        {
            File.Delete(PathOf(repoRoot, stray));
        }
        foreach (var (file, text) in emitted)
        {
            var path = PathOf(repoRoot, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        return emitted.Count;
    }

    /// <summary>Compares what is on disk with what would be emitted; answers how many findings it printed.</summary>
    public static int Check(string repoRoot)
    {
        IReadOnlyDictionary<string, string> emitted;
        try
        {
            emitted = Emit(repoRoot);
        }
        catch (LearnException stop)
        {
            Console.Error.WriteLine($"  LEARN:   {stop.Message}");
            return 1;
        }

        var stale = emitted
            .Where(pair =>
            {
                var path = PathOf(repoRoot, pair.Key);
                return !File.Exists(path) || File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal) != pair.Value;
            })
            .Select(pair => pair.Key)
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToList();
        var strays = Strays(repoRoot, emitted);
        foreach (var file in stale.Take(12))
        {
            Console.Error.WriteLine($"  DRIFT:   {file} (run `Acta.Emit docs`)");
        }
        if (stale.Count > 12)
        {
            Console.Error.WriteLine($"           and {stale.Count - 12} more of the Learn section");
        }
        foreach (var file in strays)
        {
            Console.Error.WriteLine($"  STRAY:   {file} has no program behind it (run `Acta.Emit docs`)");
        }
        if (stale.Count == 0 && strays.Count == 0 && emitted.Count > 0)
        {
            Console.WriteLine("  ok:      the how-tos match their programs, READMEs, and concepts/learn.json");
        }
        return stale.Count + strays.Count;
    }

    private static string PathOf(string repoRoot, string relative) =>
        Path.Combine(repoRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    [GeneratedRegex(@"  <url>\n    <loc>https://useacta\.net/learn/[^<]*</loc>\n  </url>\n")]
    private static partial Regex LearnEntry();

    [GeneratedRegex(@"(?:href=""|\]\()(/learn(?:/[^""#)\s]*)?)")]
    private static partial Regex LearnLink();

    [GeneratedRegex(@"[#?].*$")]
    private static partial Regex Fragment();
}
