using System.Text.RegularExpressions;
using Xunit;

namespace Acta.Tests.Learn;

/// <summary>
/// Keeps the how-to projects and their solution in sync. Every how-to project on disk must be listed in
/// <c>learn/Learn.slnx</c> (or solution builds silently skip it), and a solution path with no project on disk
/// is a stale entry. That every program has its how-to and its row in the generated table is the emitter's own check.
/// </summary>
public sealed partial class HowToIndexTests
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

    // How-to folders are named NNN-kebab and live under the category for their numeric band.
    private static readonly Regex HowToFolder = MyRegex();

    private const string SolutionPath = "learn/Learn.slnx";

    [Fact]
    public void EveryHowToProjectIsListedInTheSolution()
    {
        var repoRoot = ResolveRepoRoot();
        var solution = ReadSolution(repoRoot);

        var missing = HowToProjects(repoRoot)
            .Where(p => !solution.Contains($"\"{p.RelPath}\"", StringComparison.Ordinal))
            .Select(p => p.RelPath)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"How-to projects missing from {SolutionPath} (solution builds will skip them):\n  " + string.Join("\n  ", missing)
        );
    }

    [Fact]
    public void EverySolutionHowToPathExistsOnDisk()
    {
        var repoRoot = ResolveRepoRoot();
        var solution = ReadSolution(repoRoot);

        var stale = Regex
            .Matches(solution, @"Path=""([^.""][^""]*\.csproj)""")
            .Select(m => m.Groups[1].Value)
            .Where(rel => !File.Exists(Path.Combine(repoRoot, "learn", rel.Replace('/', Path.DirectorySeparatorChar))))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        Assert.True(stale.Count == 0, $"{SolutionPath} lists how-to projects that do not exist on disk:\n  " + string.Join("\n  ", stale));
    }

    [Fact]
    public void EveryHowToUsesItsNumericCategory()
    {
        var repoRoot = ResolveRepoRoot();
        var misplaced = HowToProjects(repoRoot)
            .Where(p => HowToFolder.IsMatch(p.Folder))
            .Select(p => (Project: p, ExpectedCategory: CategoryFolders[p.Folder[0] - '0']))
            .Where(item => !string.Equals(item.Project.Category, item.ExpectedCategory, StringComparison.Ordinal))
            .Select(item => $"{item.Project.RelPath} should be under learn/{item.ExpectedCategory}/")
            .OrderBy(message => message, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            misplaced.Count == 0,
            "How-to projects outside the category for their numeric band:\n  " + string.Join("\n  ", misplaced)
        );
    }

    [Fact]
    public void EveryCategoryHasMatchingSolutionVirtualFolder()
    {
        var repoRoot = ResolveRepoRoot();
        var solution = ReadSolution(repoRoot);
        var missing = CategoryFolders
            .Where(category => !solution.Contains($"<Folder Name=\"/{category}/\">", StringComparison.Ordinal))
            .OrderBy(category => category, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"How-to categories missing matching {SolutionPath} virtual folders:\n  " + string.Join("\n  ", missing)
        );
    }

    [Fact]
    public void EveryHowToIsInItsMatchingSolutionVirtualFolder()
    {
        var repoRoot = ResolveRepoRoot();
        var solution = ReadSolution(repoRoot);
        var misplaced = HowToProjects(repoRoot)
            .Where(project =>
            {
                var folderStart = solution.IndexOf($"<Folder Name=\"/{project.Category}/\">", StringComparison.Ordinal);
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
            $"How-to projects outside their matching {SolutionPath} virtual folders:\n  " + string.Join("\n  ", misplaced)
        );
    }

    private static string ReadSolution(string repoRoot) => File.ReadAllText(Path.Combine(repoRoot, SolutionPath));

    private static IEnumerable<(string Category, string Folder, string RelPath)> HowToProjects(string repoRoot)
    {
        var learnDir = Path.Combine(repoRoot, "learn");
        foreach (var proj in Directory.EnumerateFiles(learnDir, "*.csproj", SearchOption.AllDirectories))
        {
            var normalized = proj.Replace('\\', '/');
            if (normalized.Contains("/obj/", StringComparison.Ordinal) || normalized.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            var howToDir = Directory.GetParent(proj)!;
            var category = howToDir.Parent!.Name;
            var folder = howToDir.Name;
            var relPath = Path.GetRelativePath(learnDir, proj).Replace('\\', '/');
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
            "HowToIndexTests could not locate Acta.slnx marking the repo root from " + AppContext.BaseDirectory
        );
    }

    [GeneratedRegex(@"^\d{3}-[a-z0-9-]+$", RegexOptions.Compiled)]
    private static partial Regex MyRegex();
}
