using System.Text.RegularExpressions;
using Acta.Tests.Architecture;
using Xunit;

namespace Acta.Tests.Architecture;

/// <summary>
/// Mechanical enforcement of the production comment-style policy over the runtime core and the three
/// durable SQL providers. Five checks: no stale-marker tokens, ASCII-only comment text (bans emoji,
/// arrows, em dashes and other decorative symbols), XML doc blocks within eight lines, no comment that
/// narrates history, and no <c>&lt;remarks&gt;</c> on internal types outside the allowlist.
/// </summary>
/// <remarks>
/// Each check carries an allowlist of cleared exceptions so the policy can tighten over time:
/// a net-new violation in any file outside the allowlist fails the test, and clearing a file
/// from the allowlist after a scrub ratchets the policy. <c>docs/ideas/**</c> and
/// <c>tests/**</c> are outside the scanned roots by construction.
/// </remarks>
public sealed partial class CommentStyleTests
{
    /// <summary>
    /// Source roots subject to the comment-style policy: the runtime core plus the three durable SQL
    /// providers (matching AotPolicyTests). Acta.AspNetCore (Web-SDK dashboard host), Acta.Redis
    /// (optional wakeup), and Acta.Testing (test-support) ship too but are out of this boundary by design.
    /// </summary>
    private static readonly string[] ProductionRoots = ["src/Acta", "src/Acta.SqlServer", "src/Acta.Postgres", "src/Acta.Sqlite"];

    private const int MaxXmlDocBlockLines = 8;

    private sealed record StaleMarker(string Name, Regex Match, HashSet<string> Allowlist)
    {
        public IEnumerable<string> Violations(IEnumerable<string> files, string repoRoot) =>
            files
                .Where(f => !Allowlist.Contains(RelativeTo(repoRoot, f)))
                .Where(f => Match.IsMatch(File.ReadAllText(f)))
                .Select(f => RelativeTo(repoRoot, f));
    }

    private static readonly StaleMarker[] StaleMarkers =
    [
        new("stub", MyRegex(), Allow()),
        new("placeholder", new Regex(@"\bplaceholder\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), Allow()),
        new("future", new Regex(@"\bfuture\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), Allow()),
        new("later", new Regex(@"\blater\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), Allow()),
        new("TODO", new Regex(@"\bTODO\b", RegexOptions.Compiled), Allow()),
        new("not yet", new Regex(@"\bnot\s+yet\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), Allow()),
        new("design only", new Regex(@"\bdesign\s+only\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), Allow()),
        new("deferred no-op", new Regex(@"\bdeferred\s+no-op\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), Allow()),
        new("deferred surface", new Regex(@"\bdeferred\s+surface\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), Allow()),
        new("pre-implementation", new Regex(@"\bpre-implementation\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), Allow()),
    ];

    // Comment text must be plain ASCII: bans emoji, arrows, em/en dashes, unicode math, smart
    // quotes and any other decorative symbol in one rule, without false positives on string
    // literals (only full-line comments are scanned).
    private static readonly HashSet<string> NonAsciiAllowlist = Allow();

    // <remarks> on an internal type is reserved for concurrency / CAS / provider-difference
    // contracts; each entry here is a cleared exception of that kind. Clearing a file after a
    // scrub tightens the policy; never add a file for plain narration.
    private static readonly HashSet<string> InternalRemarksAllowlist = Allow(
        "src/Acta/Domain/Entities/StepEntity.cs",
        "src/Acta/Domain/Entities/LockEntity.cs",
        "src/Acta/Domain/Entities/ResultEntity.cs",
        "src/Acta/Domain/Entities/CheckpointEntity.cs",
        "src/Acta/Engine/Worker/Context/StepOwnershipLostException.cs",
        "src/Acta/Engine/Worker/Context/StepRetrySignal.cs",
        "src/Acta/Engine/Worker/Context/SignalSuspendSignal.cs",
        "src/Acta/Engine/Worker/Context/SuspendSignal.cs",
        "src/Acta/Engine/Alerting/AlertsJob.cs",
        "src/Acta/Engine/Recovery/RecoveryPass.cs",
        "src/Acta/Engine/Retention/RetentionJob.cs",
        "src/Acta/Domain/Jobs/JobEnqueueValues.cs",
        "src/Acta/Domain/Definitions/CatalogHash.cs",
        "src/Acta/Engine/Worker/ClockSkewValidator.cs",
        "src/Acta/Engine/Worker/JobBehaviorPipeline.cs",
        "src/Acta/Engine/Worker/JobLogScope.cs",
        "src/Acta/Engine/Telemetry/ActaMetrics.cs",
        "src/Acta/Hosting/JobTypeRegistry.cs",
        "src/Acta/Engine/Worker/WorkerContext.cs",
        "src/Acta/Engine/Worker/WorkerHeartbeat.cs",
        "src/Acta/Engine/Worker/WorkerRuntime.cs",
        "src/Acta/Engine/Worker/WorkerRuntimeHost.cs",
        "src/Acta/Engine/Worker/WorkerRuntimeInitializer.cs",
        "src/Acta/Engine/Wakeup/WorkerWakeup.cs",
        "src/Acta/Schema/SchemaModel.cs",
        "src/Acta/Schema/SchemaColumnTypes.cs"
    );

    [Fact]
    public void Production_source_contains_no_stale_markers()
    {
        var repoRoot = ResolveRepoRoot();
        var files = EnumerateProductionFiles(repoRoot).ToList();

        var failures = new List<string>();
        foreach (var marker in StaleMarkers)
        {
            foreach (var path in marker.Violations(files, repoRoot))
            {
                failures.Add($"{path}: stale marker '{marker.Name}'");
            }
        }

        AssertClean(
            failures,
            "production source contains stale markers banned in shipping packages. "
                + "For each hit: delete the comment if the code is obvious, rewrite it to describe current "
                + "behavior, or move it to docs/ideas/ if it describes future work."
        );
    }

    [Fact]
    public void Production_comments_are_plain_ascii()
    {
        var repoRoot = ResolveRepoRoot();
        var failures = new List<string>();

        foreach (var path in EnumerateProductionFiles(repoRoot))
        {
            var relative = RelativeTo(repoRoot, path);
            if (NonAsciiAllowlist.Contains(relative))
            {
                continue;
            }
            foreach (var (lineNumber, text) in CommentLines(path))
            {
                var offending = text.FirstOrDefault(c => c > 0x7F);
                if (offending != default)
                {
                    failures.Add($"{relative}({lineNumber}): non-ASCII character 'U+{(int)offending:X4}' in comment text");
                }
            }
        }

        AssertClean(
            failures,
            "comments must be plain ASCII (no emoji, arrows, em dashes, unicode math, " + "smart quotes). Rewrite the symbol in words."
        );
    }

    [Fact]
    public void Production_xml_doc_blocks_stay_short()
    {
        var repoRoot = ResolveRepoRoot();
        var oversized = new List<(string, string?)>();

        foreach (var path in EnumerateProductionFiles(repoRoot))
        {
            var blockLength = 0;
            foreach (var line in File.ReadLines(path).Append(""))
            {
                if (line.TrimStart().StartsWith("///", StringComparison.Ordinal))
                {
                    blockLength++;
                    continue;
                }
                if (blockLength > MaxXmlDocBlockLines)
                {
                    oversized.Add((Path.GetFileName(path), RelativeTo(repoRoot, path)));
                }
                blockLength = 0;
            }
        }

        ArchitectureAssert.NoViolations(
            oversized,
            $"Keep each XML doc block within {MaxXmlDocBlockLines} lines: compress it to the invariants; a public summary is one plain sentence by default."
        );
    }

    // History belongs in commit messages: a comment states what the code cannot show, never which review, run, or
    // date produced it.
    [Fact]
    public void Production_comments_do_not_narrate_history()
    {
        var repoRoot = ResolveRepoRoot();
        var narrating = EnumerateProductionFiles(repoRoot)
            .SelectMany(path =>
                CommentLines(path)
                    .Select(line => Narration().Match(line.Text))
                    .Where(match => match.Success)
                    .Select(match => $"{RelativeTo(repoRoot, path)} ({match.Value})")
            );

        AssertClean(
            [.. narrating],
            "comments narrate history. Move the history into the commit message and keep the constraint it explains."
        );
    }

    [Fact]
    public void Internal_types_carry_no_remarks_outside_allowlist()
    {
        var repoRoot = ResolveRepoRoot();
        var failures = new List<string>();

        foreach (var path in EnumerateProductionFiles(repoRoot))
        {
            var relative = RelativeTo(repoRoot, path);
            if (InternalRemarksAllowlist.Contains(relative))
            {
                continue;
            }
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                if (
                    !lines[i].TrimStart().StartsWith("///", StringComparison.Ordinal)
                    || !lines[i].Contains("<remarks>", StringComparison.Ordinal)
                )
                {
                    continue;
                }
                var declaration = FirstDeclarationAfterDocBlock(lines, i);
                if (declaration is not null && IsInternalTypeDeclaration(declaration))
                {
                    failures.Add($"{relative}({i + 1}): <remarks> on an internal type");
                }
            }
        }

        AssertClean(
            failures,
            "<remarks> on internal types is reserved for concurrency / CAS / "
                + "provider-difference contracts cleared via the allowlist. Fold the text into the "
                + "summary or delete it."
        );
    }

    [Fact]
    public void Allowlists_reference_only_existing_files()
    {
        // Stale-entry detection: every allowlisted path must resolve to a file under the scanned roots. A rename, move, or delete leaves a dead entry that silently
        // exempts nothing; flagging it keeps the ratchet accurate. Existence is the reliable signal - a
        // "does the file still trip the check" test would false-positive on the scanners' parsing limits
        // (e.g. a multi-line attribute between the doc block and an internal type declaration).
        var repoRoot = ResolveRepoRoot();
        var present = EnumerateProductionFiles(repoRoot).Select(f => RelativeTo(repoRoot, f)).ToHashSet(StringComparer.Ordinal);

        var allowlisted = StaleMarkers
            .SelectMany(m => m.Allowlist.Select(e => ($"stale-marker:{m.Name}", e)))
            .Concat(NonAsciiAllowlist.Select(e => ("non-ASCII", e)))
            .Concat(InternalRemarksAllowlist.Select(e => ("internal-remarks", e)));

        var stale = allowlisted
            .Where(x => !present.Contains(x.e))
            .Select(x => $"{x.Item1}: '{x.e}'")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        AssertClean(
            stale,
            "comment-style allowlist has stale entries: a listed file was moved, renamed, or deleted. "
                + "Update or remove the entry so the ratchet keeps tightening."
        );
    }

    private static void AssertClean(List<string> failures, string message)
    {
        if (failures.Count > 0)
        {
            Assert.Fail($"Comment-style violation: {message}\n\n{string.Join("\n", failures)}");
        }
    }

    /// <summary>
    /// Yields the text of full-line comments (lines whose first token is <c>//</c> or <c>///</c>),
    /// keeping line numbers. Inline trailing comments and string literals are not scanned, so a
    /// URL inside a string can never false-positive.
    /// </summary>
    private static IEnumerable<(int LineNumber, string Text)> CommentLines(string path)
    {
        var lines = File.ReadAllLines(path);
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                yield return (i + 1, trimmed);
            }
        }
    }

    private static string? FirstDeclarationAfterDocBlock(string[] lines, int docLineIndex)
    {
        for (var i = docLineIndex + 1; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("///", StringComparison.Ordinal) || trimmed.StartsWith('[') || trimmed.Length == 0)
            {
                continue;
            }
            return trimmed;
        }
        return null;
    }

    private static bool IsInternalTypeDeclaration(string declaration) =>
        declaration.StartsWith("internal", StringComparison.Ordinal)
        && (
            declaration.Contains(" class ")
            || declaration.Contains(" interface ")
            || declaration.Contains(" struct ")
            || declaration.Contains(" record ")
            || declaration.Contains(" enum ")
        );

    private static HashSet<string> Allow(params string[] files) => new(files.Select(Normalize), StringComparer.Ordinal);

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static string RelativeTo(string repoRoot, string path) => Normalize(Path.GetRelativePath(repoRoot, path));

    private static IEnumerable<string> EnumerateProductionFiles(string repoRoot)
    {
        foreach (var root in ProductionRoots)
        {
            var dir = Path.Combine(repoRoot, root);
            if (!Directory.Exists(dir))
            {
                continue;
            }
            foreach (var path in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                var normalized = Normalize(path);
                if (normalized.Contains("/obj/", StringComparison.Ordinal) || normalized.Contains("/bin/", StringComparison.Ordinal))
                {
                    continue;
                }
                yield return path;
            }
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
            "CommentStyleTests could not locate Acta.slnx marking the repo root from " + AppContext.BaseDirectory
        );
    }

    [GeneratedRegex(@"\bstub\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, "sl-SI")]
    private static partial Regex MyRegex();

    // A date, a release candidate, Codex, a reviewer or review finding, or a CI run.
    [GeneratedRegex(
        @"\b20\d\d-\d\d-\d\d\b|\brc\.\d+\b|\bCodex\b|\breviewers?\b|\breview findings?\b|\bCI runs?\b",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex Narration();
}
