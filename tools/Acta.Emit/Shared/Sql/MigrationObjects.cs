using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Acta.Emit.Shared.Sql;

/// <summary>One routine or operator view as a migration writes it: the installed name and the statement, without a batch separator.</summary>
internal sealed record SqlObject(string Name, string Statement)
{
    /// <summary>The snapshot's diff key: SHA-256 of the statement as written, with line endings normalized so a checkout cannot move it.</summary>
    internal string Hash => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Statement.ReplaceLineEndings("\n"))));
}

/// <summary>One dialect's routines and operator views, each set in name order, the order a migration carries them in.</summary>
internal sealed record DialectObjects(IReadOnlyList<SqlObject> Routines, IReadOnlyList<SqlObject> Views)
{
    internal IEnumerable<SqlObject> All => Routines.Concat(Views);
}

/// <summary>
/// Reads the routine and operator-view sources kept beside their operations and renders each as a migration
/// carries it: decode and <c>{{now}}</c> tokens expanded, <c>{{schema}}</c> kept for the runner to substitute.
/// </summary>
internal static class MigrationObjects
{
    private static readonly string[] SqlFolders = ["Operations", "Schema"];

    internal static IReadOnlyDictionary<string, DialectObjects> All(string repoRoot) =>
        ProviderCatalog.All.ToDictionary(p => p.Token, p => For(repoRoot, p.Dialect), StringComparer.Ordinal);

    /// <summary>A routine is a file whose first statement creates one; SQLite has none, because it runs every command inline.</summary>
    internal static DialectObjects For(string repoRoot, SqlDdlDialect dialect)
    {
        var files = SqlFiles(repoRoot, dialect.ProviderToolName).ToList();
        var routines = Named(files.Where(f => !IsView(f.Stem) && SqlResourceCatalog.DefinesRoutine(f.Body)))
            .Select(f => new SqlObject(f.Name, Render(f.Body, dialect).Trim()))
            .ToList();
        var views = Named(files.Where(f => IsView(f.Stem)))
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

    /// <summary>One dialect's files as Acta embeds them: <c>X.{token}.sql</c> under <c>Operations/</c> and <c>Schema/</c>.</summary>
    internal static IEnumerable<(string Stem, string Body)> SqlFiles(string repoRoot, string token) =>
        SqlFolders
            .Select(folder => Path.Combine(repoRoot, "src", "Acta", folder))
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, $"*.{token}.sql", SearchOption.AllDirectories))
            .Select(path => (Path.GetFileName(path).Split('.')[0], File.ReadAllText(path)));

    // A file whose stem ends in View is an operator view body, installed as x_view.
    private static bool IsView(string stem) => stem.EndsWith("View", StringComparison.Ordinal);

    private static IEnumerable<(string Name, string Body)> Named(IEnumerable<(string Stem, string Body)> files) =>
        files.Select(f => (Name: JsonNamingPolicy.SnakeCaseLower.ConvertName(f.Stem), f.Body)).OrderBy(o => o.Name, StringComparer.Ordinal);
}
