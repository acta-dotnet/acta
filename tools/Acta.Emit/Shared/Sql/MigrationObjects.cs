using System.Text.Json;

namespace Acta.Emit.Shared.Sql;

/// <summary>
/// Reads the routine and operator-view sources and renders each as a migration carries it: decode and <c>{{now}}</c>
/// tokens expanded, <c>{{schema}}</c> kept for the runner to substitute.
/// </summary>
internal static class MigrationObjects
{
    internal static IReadOnlyDictionary<string, DialectObjects> All(string repoRoot) =>
        ProviderCatalog.All.ToDictionary(p => p.Token, p => For(repoRoot, p.Dialect), StringComparer.Ordinal);

    /// <summary>
    /// A routine is an <c>X.routine.{token}.sql</c> file beside its operation, and SQLite has none, because it runs every
    /// command inline. A view is an <c>XView.{token}.sql</c> file under <c>Schema/Views/</c>.
    /// </summary>
    internal static DialectObjects For(string repoRoot, SqlDdlDialect dialect)
    {
        var token = dialect.ProviderToolName;
        var routines = Named(Files(repoRoot, "Operations", $"*.routine.{token}.sql"))
            .Select(f => new SqlObject(f.Name, Render(f.Body, dialect).Trim()))
            .ToList();
        var views = Named(Files(repoRoot, Path.Combine("Schema", "Views"), $"*View.{token}.sql"))
            .Select(f => new SqlObject(
                f.Name,
                dialect.WrapView($"{SqlDdlDialect.SchemaPlaceholder}.{f.Name}", Render(f.Body, dialect).Trim())
            ))
            .ToList();
        return new DialectObjects(routines, views);
    }

    /// <summary>
    /// Expands decode and <c>{{now}}</c> tokens, and substitutes <c>{{schema}}</c> too when <paramref name="schema"/> is
    /// given. Full-line comments keep both placeholders verbatim, so prose about a token stays true.
    /// </summary>
    internal static string Render(string sql, SqlDdlDialect dialect, string? schema = null)
    {
        if (sql.Contains("{{table", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Migrations and provision scripts never include outbox source-catalog templates.");
        }
        if (dialect.NowExpression is null && sql.Contains("{{now}}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The {{now}} token is the SQLite instant encoding; other dialects use native now().");
        }
        // Decode tokens render on the joined text so an expression wrapping a line still expands.
        var lines = sql.ReplaceLineEndings("\n")
            .Split('\n')
            .Select(line =>
                line.TrimStart().StartsWith("--", StringComparison.Ordinal)
                    ? line
                    : line.Replace(SqlDdlDialect.SchemaPlaceholder, schema ?? SqlDdlDialect.SchemaPlaceholder, StringComparison.Ordinal)
                        .Replace("{{now}}", dialect.NowExpression ?? "{{now}}", StringComparison.Ordinal)
            );
        return CodeDecodeSql.RenderDecodeTokens(string.Join("\n", lines));
    }

    /// <summary>One dialect's operation files, inline and routine alike: <c>X.{token}.sql</c> and <c>X.routine.{token}.sql</c>.</summary>
    internal static IEnumerable<(string Stem, string Body)> SqlFiles(string repoRoot, string token) =>
        Files(repoRoot, "Operations", $"*.{token}.sql");

    private static IEnumerable<(string Stem, string Body)> Files(string repoRoot, string folder, string pattern)
    {
        var root = Path.Combine(repoRoot, "src", "Acta", folder);
        return Directory.Exists(root)
            ? Directory
                .EnumerateFiles(root, pattern, SearchOption.AllDirectories)
                .Select(path => (Path.GetFileName(path).Split('.')[0], File.ReadAllText(path)))
            : [];
    }

    private static IEnumerable<(string Name, string Body)> Named(IEnumerable<(string Stem, string Body)> files) =>
        files.Select(f => (Name: JsonNamingPolicy.SnakeCaseLower.ConvertName(f.Stem), f.Body)).OrderBy(o => o.Name, StringComparer.Ordinal);
}
