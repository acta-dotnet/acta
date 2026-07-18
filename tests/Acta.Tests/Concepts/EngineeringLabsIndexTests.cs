using System.Text.RegularExpressions;
using Xunit;

namespace Acta.Tests.Concepts;

/// <summary>
/// Keeps decision-rich Engineering Labs connected to the Engineering Labs course. Ordinary concept rungs stay
/// intentionally tiny; only a README carrying the <c>engineering-lab</c> marker opts into these stronger
/// teaching invariants.
/// </summary>
public sealed partial class EngineeringLabsIndexTests
{
    private static readonly string[] RequiredHeadings =
    [
        "## The problem",
        "## Common approaches",
        "## Why this design",
        "## Trade-offs",
        "## Run the experiment",
        "## Rows to inspect",
        "## Break it",
        "## When not to use",
        "## Source trail",
    ];

    private static readonly HashSet<string> CuratedViews =
    [
        "jobs_view",
        "events_view",
        "steps_view",
        "checkpoints_view",
        "schedules_view",
        "workers_view",
        "alerts_view",
        "definitions_view",
        "tags_view",
    ];

    [Fact]
    public void EveryEngineeringLabHasCourseMetadataAndRequiredSections()
    {
        var repoRoot = ResolveRepoRoot();
        var guide = File.ReadAllText(Path.Combine(repoRoot, "docs", "engineering-labs.md"));
        var tutorials = File.ReadAllText(Path.Combine(repoRoot, "docs", "guide", "tutorials.md"));

        var failures = new List<string>();
        foreach (var lab in EngineeringLabs(repoRoot))
        {
            var relative = Path.GetRelativePath(repoRoot, lab.Readme).Replace('\\', '/');
            var metadata = MetadataRegex().Match(lab.Markdown);
            if (!metadata.Success)
            {
                failures.Add($"{relative}: malformed engineering-lab metadata block");
                continue;
            }

            foreach (var heading in RequiredHeadings.Where(heading => !lab.Markdown.Contains(heading, StringComparison.Ordinal)))
            {
                failures.Add($"{relative}: missing '{heading}'");
            }
            if (!lab.Markdown.Contains("`--all-columns`", StringComparison.Ordinal))
            {
                failures.Add($"{relative}: does not document complete-row exploration");
            }

            var primarySlug = MetadataValue(metadata.Groups["body"].Value, "lab");
            if (primarySlug is null)
            {
                failures.Add($"{relative}: metadata declares no primary lab");
            }
            if (!lab.Markdown.Contains("docs/engineering-labs.md", StringComparison.Ordinal))
            {
                failures.Add($"{relative}: source trail does not link to the Engineering Labs index");
            }

            var views =
                MetadataValue(metadata.Groups["body"].Value, "views")
                    ?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                ?? [];
            if (views.Length == 0)
            {
                failures.Add($"{relative}: metadata declares no curated views");
            }
            foreach (var view in views.Where(view => !CuratedViews.Contains(view)))
            {
                failures.Add($"{relative}: metadata references unknown curated view '{view}'");
            }

            var conceptPath = Path.GetRelativePath(repoRoot, lab.Directory).Replace('\\', '/');
            if (!guide.Contains($"(../{conceptPath}/)", StringComparison.Ordinal))
            {
                failures.Add($"{relative}: missing from the Engineering Labs index");
            }
            if (!tutorials.Contains($"(../../{conceptPath}/)", StringComparison.Ordinal))
            {
                failures.Add($"{relative}: missing from the tutorial ladder");
            }
            var labelLink = $"[`{Path.GetFileName(lab.Directory)}`](../../{conceptPath}/)";
            if (!tutorials.Contains(labelLink, StringComparison.Ordinal))
            {
                failures.Add($"{relative}: missing from the Engineering Lab label matrix");
            }
        }

        Assert.True(failures.Count == 0, "Engineering Lab course contract failures:\n  " + string.Join("\n  ", failures));
    }

    [Fact]
    public void EveryEngineeringLabUsesAnExistingCuratedViewInVisibleSql()
    {
        var repoRoot = ResolveRepoRoot();
        var missing = EngineeringLabs(repoRoot)
            .Where(lab =>
            {
                var source = string.Join(
                    "\n",
                    Directory.EnumerateFiles(lab.Directory, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText)
                );
                return !CuratedViews.Any(view => Regex.IsMatch(source, $@"\b{Regex.Escape(view)}\b", RegexOptions.IgnoreCase));
            })
            .Select(lab => Path.GetRelativePath(repoRoot, lab.Directory).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "Engineering Labs with no visible SQL reference to a curated view:\n  " + string.Join("\n  ", missing)
        );
    }

    [Fact]
    public void EveryEngineeringLabOffersAnExplicitCuratedSelectAllExploration()
    {
        var repoRoot = ResolveRepoRoot();
        var failures = new List<string>();
        foreach (var lab in EngineeringLabs(repoRoot))
        {
            var source = string.Join(
                "\n",
                Directory.EnumerateFiles(lab.Directory, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText)
            );
            var matches = SelectAllViewRegex().Matches(source);
            var relative = Path.GetRelativePath(repoRoot, lab.Directory).Replace('\\', '/');
            if (matches.Count == 0 || !source.Contains("ShowAllAsync", StringComparison.Ordinal))
            {
                failures.Add($"{relative}: missing visible ShowAllAsync SELECT * exploration query");
                continue;
            }

            foreach (var view in matches.Select(match => match.Groups["view"].Value).Where(view => !CuratedViews.Contains(view)))
            {
                failures.Add($"{relative}: SELECT * uses non-curated surface '{view}'");
            }
        }

        Assert.True(failures.Count == 0, "Engineering Lab exploration contract failures:\n  " + string.Join("\n  ", failures));
    }

    [Fact]
    public void EveryEngineeringLabKeepsEachSelectProjectionOnOneLine()
    {
        var repoRoot = ResolveRepoRoot();
        var failures = new List<string>();
        foreach (var lab in EngineeringLabs(repoRoot))
        {
            foreach (var sourcePath in Directory.EnumerateFiles(lab.Directory, "*.cs", SearchOption.AllDirectories))
            {
                var source = File.ReadAllText(sourcePath);
                foreach (Match match in LiteralSelectRegex().Matches(source))
                {
                    var firstLine = match.Groups["sql"].Value.TrimStart().Split('\n')[0].TrimEnd('\r').Trim();
                    if (string.Equals(firstLine, "SELECT", StringComparison.OrdinalIgnoreCase) || firstLine.EndsWith(','))
                    {
                        var relative = Path.GetRelativePath(repoRoot, sourcePath).Replace('\\', '/');
                        failures.Add($"{relative}: keep SELECT and its complete field list on one line: '{firstLine}'");
                    }
                }
            }
        }

        Assert.True(failures.Count == 0, "Engineering Lab SELECT style failures:\n  " + string.Join("\n  ", failures));
    }

    [Fact]
    public void EveryMarkdownConceptLinkInEngineeringLabsExists()
    {
        var repoRoot = ResolveRepoRoot();
        var docsDirectory = Path.Combine(repoRoot, "docs");
        var guide = File.ReadAllText(Path.Combine(docsDirectory, "engineering-labs.md"));
        var missing = ConceptLinkRegex()
            .Matches(guide)
            .Select(match => match.Groups["path"].Value.Replace('/', Path.DirectorySeparatorChar))
            .Select(path => (Link: path, FullPath: Path.GetFullPath(Path.Combine(docsDirectory, path))))
            .Where(item => !Directory.Exists(item.FullPath) && !File.Exists(item.FullPath))
            .Select(item => item.Link.Replace('\\', '/'))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0, "Engineering Labs links to missing concepts:\n  " + string.Join("\n  ", missing));
    }

    [Fact]
    public void CriticalEngineeringLabFailureExperimentsRemainRunnableAndHonest()
    {
        var repoRoot = ResolveRepoRoot();
        var waitSignal = ReadConcept("200-durable-execution", "204-wait-signal", "wait-signal.cs");
        var durableSleep = ReadConcept("200-durable-execution", "205-durable-sleep", "durable-sleep.cs");
        var durableStep = ReadConcept("200-durable-execution", "202-durable-step", "durable-step.cs");
        var childJobs = ReadConcept("200-durable-execution", "211-child-jobs", "child-jobs.cs");
        var scheduleMisfire = ReadConcept("100-scheduling", "106-schedule-misfire", "schedule-misfire.cs");
        var tenantScope = ReadConcept("400-observability-and-alerts", "412-tenant-scope", "tenant-scope.cs");
        var crashRecovery = ReadConcept("700-topology-and-deployment", "705-worker-crash-recovery", "worker-crash-recovery.cs");

        Assert.Contains("command == \"start\"", waitSignal, StringComparison.Ordinal);
        Assert.Contains("command == \"inspect\"", waitSignal, StringComparison.Ordinal);
        Assert.Contains("command == \"raise\"", waitSignal, StringComparison.Ordinal);
        Assert.Contains("command == \"start\"", durableSleep, StringComparison.Ordinal);
        Assert.Contains("command == \"inspect\"", durableSleep, StringComparison.Ordinal);
        Assert.Contains("command == \"recover\"", durableSleep, StringComparison.Ordinal);

        Assert.DoesNotContain("Durable run-once slot", durableStep, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prints ONCE", durableStep, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--fail-child", childJobs, StringComparison.Ordinal);
        Assert.Contains("snapshot.Status.IsTerminal", childJobs, StringComparison.Ordinal);
        Assert.DoesNotContain("GetStatusAsync(outcome) != JobStatusCode.Done", childJobs, StringComparison.Ordinal);

        Assert.DoesNotContain("DateTime.UtcNow", scheduleMisfire, StringComparison.Ordinal);
        Assert.Contains("CorrelationKey: runId", tenantScope, StringComparison.Ordinal);
        Assert.Contains("--suspend-active", tenantScope, StringComparison.Ordinal);
        Assert.Contains("SessionId", crashRecovery, StringComparison.Ordinal);

        string ReadConcept(string group, string concept, string fileName) =>
            File.ReadAllText(Path.Combine(repoRoot, "concepts", group, concept, fileName));
    }

    private static IEnumerable<(string Directory, string Readme, string Markdown)> EngineeringLabs(string repoRoot)
    {
        var concepts = Path.Combine(repoRoot, "concepts");
        foreach (var readme in Directory.EnumerateFiles(concepts, "README.md", SearchOption.AllDirectories))
        {
            var markdown = File.ReadAllText(readme);
            if (markdown.Contains("<!-- engineering-lab", StringComparison.Ordinal))
            {
                yield return (Path.GetDirectoryName(readme)!, readme, markdown);
            }
        }
    }

    private static string? MetadataValue(string body, string key)
    {
        var match = Regex.Match(body, $@"^\s*{Regex.Escape(key)}:\s*(?<value>.+?)\s*$", RegexOptions.Multiline);
        return match.Success ? match.Groups["value"].Value : null;
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
        throw new InvalidOperationException($"Could not locate Acta.slnx from {AppContext.BaseDirectory}.");
    }

    [GeneratedRegex(@"<!--\s*engineering-lab(?<body>.*?)-->", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex MetadataRegex();

    [GeneratedRegex(@"\((?<path>\.\./concepts/[^)#]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex ConceptLinkRegex();

    [GeneratedRegex(
        @"SELECT\s+\*\s+FROM\s+(?<view>[A-Za-z_][A-Za-z0-9_]*_view)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex SelectAllViewRegex();

    [GeneratedRegex(
        "\\\"\\\"\\\"(?<sql>\\s*SELECT\\b.*?)\\\"\\\"\\\"",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant
    )]
    private static partial Regex LiteralSelectRegex();
}
