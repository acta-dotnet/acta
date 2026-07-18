using System.Text.RegularExpressions;
using Acta.Features.Workers;
using Xunit;

namespace Acta.Tests.Aot;

/// <summary>
/// Mechanical enforcement of <c>docs/internals/design.md</c> § AOT and SQL parameter metadata policy.
/// Scans the AOT-compiled customer runtime (the runtime core and all three durable SQL providers) for
/// the forbidden reflection / parameter-binding APIs.
/// </summary>
/// <remarks>
/// Each forbidden pattern carries an allowlist of source files that legitimately use it; adding a
/// forbidden pattern to a new file fails the test, and the only way past it is to refactor to the
/// generated path or extend the allowlist with a justifying comment. The scan is path-based: tests,
/// generators, the emit CLI, and generated <c>obj/Generated/</c> output are excluded by construction.
/// </remarks>
public sealed class AotPolicyTests
{
    /// <summary>
    /// Source roots subject to the no-reflection policy: the runtime core plus the three durable SQL
    /// providers (Postgres, SQL Server, SQLite), i.e. what compiles into a customer's Native AOT worker
    /// or enqueue host. The other shipping packages are out of this boundary by design: Acta.AspNetCore
    /// is an ASP.NET Web-SDK dashboard host (not an AOT target), Acta.Redis is an optional wakeup
    /// accelerator, and Acta.Testing is test-support that runs in test hosts, not the production runtime.
    /// </summary>
    private static readonly string[] RuntimeRoots =
    [
        "src/Acta.Contracts",
        "src/Acta.Relational",
        "src/Acta",
        "src/Acta.SqlServer",
        "src/Acta.Postgres",
        "src/Acta.Sqlite",
    ];

    /// <summary>
    /// One forbidden API pattern + the per-file allowlist of legitimate current callers.
    /// </summary>
    private sealed record ForbiddenPattern(string Name, Regex Match, HashSet<string> Allowlist)
    {
        public IEnumerable<string> Violations(IEnumerable<string> files, string repoRoot) =>
            files
                .Where(f => !Allowlist.Contains(RelativeTo(repoRoot, f)))
                .Where(f => Match.IsMatch(File.ReadAllText(f)))
                .Select(f => RelativeTo(repoRoot, f));
    }

    private static readonly ForbiddenPattern[] ReflectionPatterns =
    [
        new("Assembly.GetTypes()", new Regex(@"\.GetTypes\s*\(", RegexOptions.Compiled), Allow()),
        // The worker runtime reads AssemblyInformationalVersion from the entry assembly to stamp
        // the worker's deployment version. This is one-time startup metadata, not hot-path dispatch.
        new(
            "GetCustomAttribute",
            new Regex(@"\bGetCustomAttribute\b", RegexOptions.Compiled),
            Allow("src/Acta/Features/Workers/WorkerRuntimeInitializer.cs")
        ),
        new("MakeGenericMethod", new Regex(@"\bMakeGenericMethod\b", RegexOptions.Compiled), Allow()),
        new("Activator.CreateInstance", new Regex(@"\bActivator\.CreateInstance\b", RegexOptions.Compiled), Allow()),
        new("Expression.Compile", new Regex(@"\bExpression\.Compile\b", RegexOptions.Compiled), Allow()),
        new("MethodInfo.Invoke", new Regex(@"\bMethodInfo\.Invoke\b", RegexOptions.Compiled), Allow()),
        new("MethodInfo field/local", new Regex(@"\bMethodInfo\b", RegexOptions.Compiled), Allow()),
        // Dispatch routes through generator-emitted per-handler delegates; this name has no
        // remaining caller and must not return.
        new("ReflectionJobInvoker reference", new Regex(@"\bReflectionJobInvoker\b", RegexOptions.Compiled), Allow()),
    ];

    private static readonly ForbiddenPattern[] ParameterBindingPatterns =
    [
        // AddWithValue infers SqlDbType from the CLR value's type which breaks plan-cache stability
        // and round-trips nvarchar(max) / varbinary(max) wrong. Banned everywhere in runtime code.
        new("AddWithValue", new Regex(@"\bAddWithValue\s*\(", RegexOptions.Compiled), Allow()),
        // The value-only SqlParameter constructor has the same type-inference hazard, but the two-arg
        // forms can't be distinguished by regex alone, so the strict catch is left to code review.
    ];

    private static readonly ForbiddenPattern[] DataAccessSurfacePatterns =
    [
        new("IDbCommandExecutor", new Regex(@"\bIDbCommandExecutor\b", RegexOptions.Compiled), Allow()),
        new("IDbExecutionScope", new Regex(@"\bIDbExecutionScope\b", RegexOptions.Compiled), Allow()),
        new("IDbConnectionFactory", new Regex(@"\bIDbConnectionFactory\b", RegexOptions.Compiled), Allow()),
        new("IDbSqlSyntax", new Regex(@"\bIDbSqlSyntax\b", RegexOptions.Compiled), Allow()),
        new("IProviderSqlSource", new Regex(@"\bIProviderSqlSource\b", RegexOptions.Compiled), Allow()),
        new("DbSqlCommand", new Regex(@"\bDbSqlCommand\b", RegexOptions.Compiled), Allow()),
        new("raw connection bridge", new Regex(@"\bIDbConnectionSession\b", RegexOptions.Compiled), Allow()),
        new("Acta.Testing raw connection helper", new Regex(@"\bGetConnectionAsync\s*\(", RegexOptions.Compiled), Allow()),
        new("RuntimeParam", new Regex(@"\bRuntimeParam\b", RegexOptions.Compiled), Allow()),
        new("P parameter catalog", new Regex(@"\bP\.", RegexOptions.Compiled), Allow()),
        // SqliteDialect begins an immediate transaction so the inline-only provider's multi-statement
        // write body is atomic (same rationale as the commit/rollback allowlist below); the migration
        // runner opens one to apply DDL. Routine providers (PG/MSSQL) never take this path.
        new(
            "C# transaction begin/open",
            new Regex(@"\b(BeginTransaction|OpenTransaction)\b", RegexOptions.Compiled),
            Allow("src/Acta.Relational/Schema/SchemaMigrationRunner.cs", "src/Acta.Sqlite/SqliteDialect.cs")
        ),
        // DbSession wraps execute-style calls in a write transaction ONLY for an inline-only provider
        // (SQLite) that has no stored routine to make its multi-statement write body atomic; routine
        // providers (PG/MSSQL) keep single-CALL atomicity and never enter that path.
        // DbSession owns the single inline-provider write transaction for every shared store; the
        // migration runner opens one to apply DDL. No provider store commits directly any more.
        new(
            "C# transaction commit",
            new Regex(@"\bCommitAsync\b", RegexOptions.Compiled),
            Allow("src/Acta.Relational/Schema/SchemaMigrationRunner.cs", "src/Acta.Relational/Connections/DbSession.cs")
        ),
        new(
            "C# transaction rollback",
            new Regex(@"\bRollbackAsync\b", RegexOptions.Compiled),
            Allow("src/Acta.Relational/Schema/SchemaMigrationRunner.cs")
        ),
        new(
            "direct DbParameterSpec scalar factory",
            new Regex(@"\bDbParameterSpec\.(Int16|Int32|Int64|Guid|Byte|CodeByte|CodeShort)\s*\(", RegexOptions.Compiled),
            Allow()
        ),
        new("direct DbParameterSpec.ForColumn", new Regex(@"\bDbParameterSpec\.ForColumn\s*\(", RegexOptions.Compiled), Allow()),
    ];

    private static readonly ForbiddenPattern[] ProjectionMaterializationPatterns =
    [
        new("runtime projection materializer", new Regex(@"\bMaterializeProjection\b", RegexOptions.Compiled), Allow()),
        new("projection constructor discovery", new Regex(@"\bGetConstructors?\s*\(", RegexOptions.Compiled), Allow()),
        new("projection property discovery", new Regex(@"\bGetProperties\s*\(", RegexOptions.Compiled), Allow()),
    ];

    /// <summary>
    /// Markers the surface-discipline policy forbids in runtime code: speculative placeholders,
    /// design-sketch prose, and <c>throw new NotImplementedException</c> stubs. Every public type
    /// either works today or has been parked under <c>docs/ideas/</c>.
    /// </summary>
    private static readonly ForbiddenPattern[] SupportedSurfacePatterns =
    [
        new("throw new NotImplementedException", new Regex(@"\bthrow\s+new\s+NotImplementedException\b", RegexOptions.Compiled), Allow()),
        new("Placeholder marker", new Regex(@"\bPlaceholder\b", RegexOptions.Compiled), Allow()),
        new("release stub", new Regex(@"\brelease\s+stub\b", RegexOptions.Compiled), Allow()),
        new("Lifts during source-lift sweep", new Regex(@"Lifts\s+during\s+source-lift\s+sweep", RegexOptions.Compiled), Allow()),
        new("Design sketch", new Regex(@"\bDesign\s+sketch\b", RegexOptions.Compiled), Allow()),
        new("deferred no-op", new Regex(@"\bdeferred\s+no-op\b", RegexOptions.Compiled), Allow()),
    ];

    [Fact]
    public void Runtime_source_uses_no_forbidden_reflection_APIs()
    {
        var repoRoot = ResolveRepoRoot();
        var files = EnumerateRuntimeFiles(repoRoot).ToList();

        var failures = new List<string>();
        foreach (var pattern in ReflectionPatterns)
        {
            foreach (var path in pattern.Violations(files, repoRoot))
            {
                failures.Add($"{path}: forbidden reflection API '{pattern.Name}'");
            }
        }

        if (failures.Count > 0)
        {
            Assert.Fail(
                "AOT policy violation — runtime source touches reflection APIs outside the documented allowlist. "
                    + "Either route through a source generator or, if the reflection use is intentional and bounded, "
                    + "add the file to the matching pattern's Allowlist in AotPolicyTests with a justifying comment.\n\n"
                    + string.Join("\n", failures)
            );
        }
    }

    [Fact]
    public void Runtime_source_uses_no_forbidden_parameter_binding_APIs()
    {
        var repoRoot = ResolveRepoRoot();
        var files = EnumerateRuntimeFiles(repoRoot).ToList();

        var failures = new List<string>();
        foreach (var pattern in ParameterBindingPatterns)
        {
            foreach (var path in pattern.Violations(files, repoRoot))
            {
                failures.Add($"{path}: forbidden parameter-binding API '{pattern.Name}'");
            }
        }

        if (failures.Count > 0)
        {
            Assert.Fail(
                "AOT policy violation — runtime source uses parameter-binding APIs the policy bans "
                    + "(see docs/internals/design.md § AOT and SQL parameter metadata policy).\n\n"
                    + string.Join("\n", failures)
            );
        }
    }

    [Fact]
    public void Runtime_source_uses_only_supported_data_access_surface()
    {
        var repoRoot = ResolveRepoRoot();
        var files = EnumerateRuntimeFiles(repoRoot).ToList();

        var failures = new List<string>();
        foreach (var pattern in DataAccessSurfacePatterns)
        {
            foreach (var path in pattern.Violations(files, repoRoot))
            {
                failures.Add($"{path}: forbidden data-access surface '{pattern.Name}'");
            }
        }

        if (failures.Count > 0)
        {
            Assert.Fail(
                "Data-access policy violation — runtime source uses APIs outside the supported DbSession + named SQL + DbParams.For surface.\n\n"
                    + string.Join("\n", failures)
            );
        }
    }

    [Fact]
    public void Runtime_source_uses_no_runtime_projection_materialization()
    {
        var repoRoot = ResolveRepoRoot();
        var files = EnumerateRuntimeFiles(repoRoot).ToList();

        var failures = new List<string>();
        foreach (var pattern in ProjectionMaterializationPatterns)
        {
            foreach (var path in pattern.Violations(files, repoRoot))
            {
                failures.Add($"{path}: forbidden projection materialization API '{pattern.Name}'");
            }
        }

        if (failures.Count > 0)
        {
            Assert.Fail(
                "AOT policy violation — runtime projection rows must use generated [DbProjection] binders, "
                    + "not constructor/property discovery or ad hoc projection materializers.\n\n"
                    + string.Join("\n", failures)
            );
        }
    }

    [Fact]
    public void Runtime_source_contains_no_unsupported_surface_markers()
    {
        var repoRoot = ResolveRepoRoot();
        var files = EnumerateRuntimeFiles(repoRoot).ToList();

        var failures = new List<string>();
        foreach (var pattern in SupportedSurfacePatterns)
        {
            foreach (var path in pattern.Violations(files, repoRoot))
            {
                failures.Add($"{path}: unsupported-surface marker '{pattern.Name}'");
            }
        }

        if (failures.Count > 0)
        {
            Assert.Fail(
                "Surface-discipline violation — runtime source contains placeholder / stub markers "
                    + "the supported-surface policy forbids. Either delete the stub, implement it, or park the "
                    + "intent under docs/ideas/ and remove the source-level claim. If the gap is intentional "
                    + "and tracked, extend the matching SupportedSurfacePatterns allowlist in "
                    + "AotPolicyTests with a justifying comment.\n\n"
                    + string.Join("\n", failures)
            );
        }
    }

    [Fact]
    public void Allowlists_reference_only_existing_files()
    {
        // Every allowlisted path must resolve to a file
        // under the scanned roots. A rename, move, or delete leaves a dead entry that silently exempts
        // nothing; flagging it keeps the ratchet honest. Existence is the reliable signal - a "does the
        // file still trip the pattern" check would false-positive on the scanners' own parsing limits.
        var repoRoot = ResolveRepoRoot();
        var present = EnumerateRuntimeFiles(repoRoot).Select(f => RelativeTo(repoRoot, f)).ToHashSet(StringComparer.Ordinal);

        var stale = AllPatterns()
            .SelectMany(p => p.Allowlist.Select(entry => (p.Name, entry)))
            .Where(x => !present.Contains(x.entry))
            .Select(x => $"{x.Name}: '{x.entry}'")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        if (stale.Count > 0)
        {
            Assert.Fail(
                "AOT-policy allowlist has stale entries — a listed file was moved, renamed, or deleted. "
                    + "Update or remove the entry so the ratchet keeps tightening.\n\n"
                    + string.Join("\n", stale)
            );
        }
    }

    private static IEnumerable<ForbiddenPattern> AllPatterns() =>
        [
            .. ReflectionPatterns,
            .. ParameterBindingPatterns,
            .. DataAccessSurfacePatterns,
            .. ProjectionMaterializationPatterns,
            .. SupportedSurfacePatterns,
        ];

    private static HashSet<string> Allow(params string[] files) => new(files.Select(Normalize), StringComparer.Ordinal);

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static string RelativeTo(string repoRoot, string path) => Normalize(Path.GetRelativePath(repoRoot, path));

    private static IEnumerable<string> EnumerateRuntimeFiles(string repoRoot)
    {
        foreach (var root in RuntimeRoots)
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

    /// <summary>
    /// Walks up from the test assembly until it hits a directory containing the solution file; the AOT
    /// scan needs source files, not built DLLs, so it must address the repo, not <c>bin/</c>.
    /// </summary>
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
            "AotPolicyTests could not locate the Acta.slnx marking the repo root from " + AppContext.BaseDirectory
        );
    }
}
