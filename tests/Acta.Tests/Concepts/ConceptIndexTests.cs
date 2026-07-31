using System.Text.RegularExpressions;
using Xunit;

namespace Acta.Tests.Concepts;

/// <summary>
/// Keeps the concept ladder, the solution, and the tutorial index in sync. Every concept project on
/// disk must be listed in <c>Acta.slnx</c> (or solution builds silently skip it) and every
/// numbered rung must have a row in <c>docs/guide/tutorials.md</c> (or it is undiscoverable). The reverse
/// direction is checked too: a solution path with no project on disk is a stale entry.
/// </summary>
public sealed partial class ConceptIndexTests
{
    private static readonly string[] CategoryFolders =
    [
        "000-fundamentals",
        "100-scheduling",
        "200-durable-execution",
        "300-failure-and-recovery",
        "400-observability-and-alerts",
        "500-payloads",
        "600-job-composition",
        "700-topology-and-deployment",
        "800-testing",
        "900-runtime-and-tuning",
    ];

    // Concept folders are named NNN-kebab and live under the category for their numeric band.
    private static readonly Regex RungFolder = MyRegex();

    [Fact]
    public void EveryConceptProjectIsListedInTheSolution()
    {
        var repoRoot = ResolveRepoRoot();
        var solution = File.ReadAllText(Path.Combine(repoRoot, "Acta.slnx"));

        var missing = ConceptProjects(repoRoot)
            .Where(p => !solution.Contains($"\"{p.RelPath}\"", StringComparison.Ordinal))
            .Select(p => p.RelPath)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "Concept projects missing from Acta.slnx (solution builds will skip them):\n  " + string.Join("\n  ", missing)
        );
    }

    [Fact]
    public void EveryNumberedRungHasATutorialRow()
    {
        var repoRoot = ResolveRepoRoot();
        var tutorials = File.ReadAllText(Path.Combine(repoRoot, "docs", "guide", "tutorials.md"));

        var missing = ConceptProjects(repoRoot)
            .Where(p => RungFolder.IsMatch(p.Folder))
            .Where(p => !tutorials.Contains($"concepts/{p.Category}/{p.Folder}/)", StringComparison.Ordinal))
            .Select(p => $"{p.Category}/{p.Folder}")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "Concept rungs missing a docs/guide/tutorials.md ladder row (undiscoverable):\n  " + string.Join("\n  ", missing)
        );
    }

    [Fact]
    public void EverySolutionConceptPathExistsOnDisk()
    {
        var repoRoot = ResolveRepoRoot();
        var solution = File.ReadAllText(Path.Combine(repoRoot, "Acta.slnx"));

        var stale = Regex
            .Matches(solution, @"Path=""(concepts/[^""]+\.csproj)""")
            .Select(m => m.Groups[1].Value)
            .Where(rel => !File.Exists(Path.Combine(repoRoot, rel.Replace('/', Path.DirectorySeparatorChar))))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        Assert.True(stale.Count == 0, "Acta.slnx lists concept projects that do not exist on disk:\n  " + string.Join("\n  ", stale));
    }

    [Fact]
    public void EveryConceptUsesItsNumericCategory()
    {
        var repoRoot = ResolveRepoRoot();
        var misplaced = ConceptProjects(repoRoot)
            .Where(p => RungFolder.IsMatch(p.Folder))
            .Select(p => (Project: p, ExpectedCategory: CategoryFolders[p.Folder[0] - '0']))
            .Where(item => !string.Equals(item.Project.Category, item.ExpectedCategory, StringComparison.Ordinal))
            .Select(item => $"{item.Project.RelPath} should be under concepts/{item.ExpectedCategory}/")
            .OrderBy(message => message, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            misplaced.Count == 0,
            "Concept projects outside the category for their numeric band:\n  " + string.Join("\n  ", misplaced)
        );
    }

    [Fact]
    public void EveryCategoryHasMatchingSolutionVirtualFolder()
    {
        var repoRoot = ResolveRepoRoot();
        var solution = File.ReadAllText(Path.Combine(repoRoot, "Acta.slnx"));
        var missing = CategoryFolders
            .Where(category => !solution.Contains($"<Folder Name=\"/concepts/{category}/\">", StringComparison.Ordinal))
            .OrderBy(category => category, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "Concept categories missing matching Acta.slnx virtual folders:\n  " + string.Join("\n  ", missing)
        );
    }

    [Fact]
    public void EveryConceptIsInItsMatchingSolutionVirtualFolder()
    {
        var repoRoot = ResolveRepoRoot();
        var solution = File.ReadAllText(Path.Combine(repoRoot, "Acta.slnx"));
        var misplaced = ConceptProjects(repoRoot)
            .Where(project =>
            {
                var folderStart = solution.IndexOf($"<Folder Name=\"/concepts/{project.Category}/\">", StringComparison.Ordinal);
                var folderEnd = folderStart < 0 ? -1 : solution.IndexOf("</Folder>", folderStart, StringComparison.Ordinal);
                var projectEntry = $"<Project Path=\"{project.RelPath}\" />";
                var projectIndex = solution.IndexOf(projectEntry, StringComparison.Ordinal);
                return folderStart < 0 || folderEnd < 0 || projectIndex < folderStart || projectIndex > folderEnd;
            })
            .Select(project => project.RelPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            misplaced.Count == 0,
            "Concept projects outside their matching Acta.slnx virtual folders:\n  " + string.Join("\n  ", misplaced)
        );
    }

    private static IEnumerable<(string Category, string Folder, string RelPath)> ConceptProjects(string repoRoot)
    {
        var conceptsDir = Path.Combine(repoRoot, "concepts");
        foreach (var proj in Directory.EnumerateFiles(conceptsDir, "*.csproj", SearchOption.AllDirectories))
        {
            var normalized = proj.Replace('\\', '/');
            if (normalized.Contains("/obj/", StringComparison.Ordinal) || normalized.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            var rungDir = Directory.GetParent(proj)!;
            var category = rungDir.Parent!.Name;
            var folder = rungDir.Name;
            var relPath = Path.GetRelativePath(repoRoot, proj).Replace('\\', '/');
            yield return (category, folder, relPath);
        }
    }

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
            "ConceptIndexTests could not locate Acta.slnx marking the repo root from " + AppContext.BaseDirectory
        );
    }

    [GeneratedRegex(@"^\d{3}-[a-z0-9-]+$", RegexOptions.Compiled)]
    private static partial Regex MyRegex();
}
