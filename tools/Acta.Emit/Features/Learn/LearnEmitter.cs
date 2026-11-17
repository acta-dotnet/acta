using System.Text.RegularExpressions;
using Acta.Emit.Features.Verify;

namespace Acta.Emit.Features.Learn;

/// <summary>
/// The Learn section as files: one page, one Markdown twin and one program file per how-to, the section's front page,
/// the index for agents, the section's sitemap, and the table of how-tos in the reference docs. Nothing here is edited
/// by hand: a change to a program, a README or the sections file changes what is emitted.
/// </summary>
internal static partial class LearnEmitter
{
    private const string Pages = "site/learn";

    /// <summary>The tutorial under <c>site/learn/</c> is written by hand, and lends the how-tos its header and footer.</summary>
    private static readonly string HandKept = Path.GetFileName(LearnPages.Tutorial);

    /// <summary>
    /// Every emitted file by its repo-relative path. A root with no programs has no Learn section, which is what the
    /// throwaway roots of the schema commands' tests are.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Emit(string repoRoot)
    {
        var emitted = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(LearnLibrary.PathOf(repoRoot, LearnLibrary.Root)))
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
        emitted[$"{Pages}/sitemap.xml"] = pages.SiteMap();
        emitted[LearnPages.Table] = pages.TableDocument();

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

    /// <summary>Emitted files that are missing on disk or no longer what would be emitted.</summary>
    public static IReadOnlyList<string> Stale(string repoRoot, IReadOnlyDictionary<string, string> emitted) =>
        emitted
            .Where(pair =>
            {
                var path = LearnLibrary.PathOf(repoRoot, pair.Key);
                return !File.Exists(path) || !CheckCommand.NewlineEqual(File.ReadAllText(path), pair.Value);
            })
            .Select(pair => pair.Key)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>Files under <c>site/learn/</c> that no program stands behind any more.</summary>
    public static IReadOnlyList<string> Strays(string repoRoot, IReadOnlyDictionary<string, string> emitted)
    {
        var directory = LearnLibrary.PathOf(repoRoot, Pages);
        if (emitted.Count == 0 || !Directory.Exists(directory))
        {
            return [];
        }
        return Directory
            .EnumerateFiles(directory)
            .Select(path => Path.GetFileName(path)!)
            .Where(name => name != HandKept && !emitted.ContainsKey($"{Pages}/{name}"))
            .Select(name => $"{Pages}/{name}")
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Writes the section and removes its strays; answers the exit code.</summary>
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
            return 1;
        }

        foreach (var stray in Strays(repoRoot, emitted))
        {
            File.Delete(LearnLibrary.PathOf(repoRoot, stray));
        }
        foreach (var (file, text) in emitted)
        {
            var path = LearnLibrary.PathOf(repoRoot, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        if (emitted.Count > 0)
        {
            Console.WriteLine($"  wrote {emitted.Count} files of the Learn section: the how-tos under {Pages} and {LearnPages.Table}");
        }
        return 0;
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

        var stale = Stale(repoRoot, emitted);
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
            Console.WriteLine($"  ok:      the how-tos match their programs, READMEs, and {LearnLibrary.SectionsFile}");
        }
        return stale.Count + strays.Count;
    }

    [GeneratedRegex(@"(?:href=""|\]\()(/learn(?:/[^""#)\s]*)?)")]
    private static partial Regex LearnLink();

    [GeneratedRegex(@"[#?].*$")]
    private static partial Regex Fragment();
}
