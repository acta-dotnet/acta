using System.Text.RegularExpressions;
using Acta.Modules.Execution;
using Acta.Modules.Execution.Definitions;
using Acta.Modules.Execution.Workers;
using Xunit;

namespace Acta.Tests.Aot;

/// <summary>
/// Mechanical enforcement of the production comment-style policy over the runtime core and the three
/// durable SQL providers. Four checks: no stale-marker tokens, ASCII-only comment text (bans emoji,
/// arrows, em dashes and other decorative symbols), no oversized XML doc blocks, and no
/// <c>&lt;remarks&gt;</c> on internal types outside the allowlist.
/// </summary>
/// <remarks>
/// Each check carries an allowlist of cleared exceptions so the policy can tighten over time:
/// a net-new violation in any file outside the allowlist fails the test, and clearing a file
/// from the allowlist after a scrub ratchets the policy. <c>docs/ideas/**</c> and
/// <c>tests/**</c> are outside the scanned roots by construction.
/// </remarks>
public sealed class CommentStyleTests
{
    /// <summary>
    /// Source roots subject to the comment-style policy: the runtime core plus the three durable SQL
    /// providers (matching AotPolicyTests). Acta.AspNetCore (Web-SDK dashboard host), Acta.Redis
    /// (optional wakeup), and Acta.Testing (test-support) ship too but are out of this boundary by design.
    /// </summary>
    private static readonly string[] ProductionRoots =
    [
        "src/Acta",
        "src/Acta.Relational",
        "src/Acta.Runtime",
        "src/Acta.SqlServer",
        "src/Acta.Postgres",
        "src/Acta.Sqlite",
    ];

    private const int MaxXmlDocBlockLines = 16;

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
        new("stub", new Regex(@"\bstub\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), Allow()),
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

    private static readonly HashSet<string> OversizedDocAllowlist = Allow();

    // <remarks> on an internal type is reserved for concurrency / CAS / provider-difference
    // contracts; each entry here is a cleared exception of that kind. Clearing a file after a
    // scrub tightens the policy; never add a file for plain narration.
    private static readonly HashSet<string> InternalRemarksAllowlist = Allow(
        "src/Acta.Relational/Entities/JobStep.cs",
        "src/Acta.Relational/Entities/Lease.cs",
        "src/Acta.Relational/Entities/JobResult.cs",
        "src/Acta.Relational/Entities/JobCheckpoint.cs",
        "src/Acta.Runtime/Modules/Execution/StepOwnershipLostException.cs",
        "src/Acta.Runtime/Modules/Execution/StepRetrySignal.cs",
        "src/Acta.Runtime/Modules/Execution/Signals/SignalSuspendSignal.cs",
        "src/Acta.Runtime/Modules/Execution/SuspendSignal.cs",
        "src/Acta.Runtime/Modules/Alerting/AlertsJob.cs",
        "src/Acta.Runtime/Modules/Execution/RecoveryJob.cs",
        "src/Acta.Runtime/Maintenance/RetentionJob.cs",
        "src/Acta.Runtime/Modules/Execution/CompletionTypes.cs",
        "src/Acta.Runtime/Modules/Execution/Jobs/JobEnqueueRow.cs",
        "src/Acta.Runtime/Modules/Execution/Definitions/DefinitionRows.cs",
        "src/Acta.Runtime/Modules/Execution/Definitions/CatalogHash.cs",
        "src/Acta.Runtime/Modules/Execution/Workers/ClockSkewValidator.cs",
        "src/Acta.Runtime/Modules/Execution/JobBehaviorPipeline.cs",
        "src/Acta.Runtime/Modules/Execution/Definitions/JobDefinitionRegistration.cs",
        "src/Acta.Runtime/Modules/Execution/JobLogScope.cs",
        "src/Acta.Runtime/Modules/Execution/JobMetrics.cs",
        "src/Acta.Runtime/Modules/Execution/Definitions/JobTypeIndex.cs",
        "src/Acta.Runtime/Modules/Execution/Workers/WorkerContext.cs",
        "src/Acta.Runtime/Modules/Execution/Workers/WorkerHeartbeat.cs",
        "src/Acta.Runtime/Modules/Execution/Workers/WorkerRegistration.cs",
        "src/Acta.Runtime/Modules/Execution/Workers/WorkerRuntime.cs",
        "src/Acta.Runtime/Modules/Execution/Workers/WorkerRuntimeHost.cs",
        "src/Acta.Runtime/Modules/Execution/Workers/WorkerRuntimeInitializer.cs",
        "src/Acta.Runtime/Modules/Execution/Workers/WorkerWakeup.cs",
        "src/Acta.Relational/Schema/SchemaModel.cs",
        "src/Acta.Relational/Schema/SchemaColumnTypes.cs"
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
        var failures = new List<string>();

        foreach (var path in EnumerateProductionFiles(repoRoot))
        {
            var relative = RelativeTo(repoRoot, path);
            if (OversizedDocAllowlist.Contains(relative))
            {
                continue;
            }
            var lines = File.ReadAllLines(path);
            var blockStart = -1;
            var blockLength = 0;
            for (var i = 0; i <= lines.Length; i++)
            {
                var isDocLine = i < lines.Length && lines[i].TrimStart().StartsWith("///", StringComparison.Ordinal);
                if (isDocLine)
                {
                    if (blockLength == 0)
                    {
                        blockStart = i + 1;
                    }
                    blockLength++;
                    continue;
                }
                if (blockLength > MaxXmlDocBlockLines)
                {
                    failures.Add($"{relative}({blockStart}): XML doc block is {blockLength} lines (max {MaxXmlDocBlockLines})");
                }
                blockLength = 0;
            }
        }

        AssertClean(
            failures,
            $"XML doc blocks must stay at or under {MaxXmlDocBlockLines} lines. "
                + "Compress to the invariants; a public summary is one plain sentence by default."
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
        // Stale-entry detection (cf. StoreContractCoverageTests): every allowlisted path must resolve to a
        // file under the scanned roots. A rename, move, or delete leaves a dead entry that silently
        // exempts nothing; flagging it keeps the ratchet honest. Existence is the reliable signal - a
        // "does the file still trip the check" test would false-positive on the scanners' parsing limits
        // (e.g. a multi-line attribute between the doc block and an internal type declaration).
        var repoRoot = ResolveRepoRoot();
        var present = EnumerateProductionFiles(repoRoot).Select(f => RelativeTo(repoRoot, f)).ToHashSet(StringComparer.Ordinal);

        var allowlisted = StaleMarkers
            .SelectMany(m => m.Allowlist.Select(e => ($"stale-marker:{m.Name}", e)))
            .Concat(NonAsciiAllowlist.Select(e => ("non-ASCII", e)))
            .Concat(OversizedDocAllowlist.Select(e => ("oversized-doc", e)))
            .Concat(InternalRemarksAllowlist.Select(e => ("internal-remarks", e)));

        var stale = allowlisted
            .Where(x => !present.Contains(x.Item2))
            .Select(x => $"{x.Item1}: '{x.Item2}'")
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
            if (
                trimmed.StartsWith("///", StringComparison.Ordinal)
                || trimmed.StartsWith("[", StringComparison.Ordinal)
                || trimmed.Length == 0
            )
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
}
