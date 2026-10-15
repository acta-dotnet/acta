using Acta.Tests.Conformance.Testing;

namespace Acta.Tests.Conformance.Sql;

/// <summary>
/// Reads one dialect's operation SQL from the source tree, the <c>X.{dialect}.sql</c> and <c>X.routine.{dialect}.sql</c>
/// files under <c>src/Acta/Operations/</c>, and on request the provider's ordered migrations. Logical paths are relative
/// to the project that owns the file: <c>Operations/Jobs/PauseJob/PauseJob.pg.sql</c>, <c>Schema/Migrations/M20261015120000_init.sql</c>.
/// </summary>
internal static class ProviderSqlResources
{
    public static string DialectFromPrefix(string providerResourcePrefix) =>
        providerResourcePrefix.Contains("SqlServer", StringComparison.Ordinal) ? "mssql"
        : providerResourcePrefix.Contains("Postgres", StringComparison.Ordinal) ? "pg"
        : providerResourcePrefix.Contains("Sqlite", StringComparison.Ordinal) ? "sqlite"
        : throw new InvalidOperationException($"Cannot derive a dialect token from resource prefix '{providerResourcePrefix}'.");

    public static IEnumerable<(string LogicalPath, string Sql)> Enumerate(string dialectToken, bool includeMigrations = false)
    {
        var src = Path.Combine(IntegrationConfig.FindRepoRoot(), "src");
        var files = Files(Path.Combine(src, "Acta"), "Operations", $"*.{dialectToken}.sql");
        if (includeMigrations)
        {
            files = files.Concat(
                Files(Path.Combine(src, ProviderAssemblyName(dialectToken)), Path.Combine("Schema", "Migrations"), "M*.sql")
            );
        }
        return files.Select(file => (file.Logical, File.ReadAllText(file.Path)));
    }

    public static readonly string[] Dialects = ["pg", "mssql", "sqlite"];

    /// <summary>Every stem of one dialect.</summary>
    public static IEnumerable<string> Stems(string dialectToken) => Enumerate(dialectToken).Select(r => Stem(r.LogicalPath));

    /// <summary>The SQL of one dialect's file with this stem.</summary>
    public static string Read(string dialectToken, string stem) => Enumerate(dialectToken).Single(r => Stem(r.LogicalPath) == stem).Sql;

    /// <summary>The file stem of a logical path, the name a caller addresses it by: <c>PauseJob</c>.</summary>
    public static string Stem(string logicalPath) => Path.GetFileName(logicalPath).Split('.')[0];

    public static string ProviderAssemblyName(string dialectToken) =>
        dialectToken switch
        {
            "sqlite" => "Acta.Sqlite",
            "pg" => "Acta.Postgres",
            "mssql" => "Acta.SqlServer",
            _ => throw new InvalidOperationException($"No provider package known for dialect token '{dialectToken}'."),
        };

    // In ordinal path order, so a failure list reads the same on every machine.
    private static IEnumerable<(string Path, string Logical)> Files(string project, string folder, string pattern) =>
        Directory
            .EnumerateFiles(Path.Combine(project, folder), pattern, SearchOption.AllDirectories)
            .Select(path => (Path: path, Logical: Path.GetRelativePath(project, path).Replace('\\', '/')))
            .OrderBy(file => file.Logical, StringComparer.Ordinal);
}
