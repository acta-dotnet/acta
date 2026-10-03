using System.Reflection;

namespace Acta.Tests.Conformance.Sql;

/// <summary>
/// Enumerates one dialect's executable SQL as the catalog sees it: the <c>X.{dialect}.sql</c> files
/// <c>Acta.Runtime</c> embeds, the provider's own <c>Sql/</c> tree until the move beside the slices
/// lands, and on request the provider's ordered migrations.
/// </summary>
internal static class ProviderSqlResources
{
    public static string DialectFromPrefix(string providerResourcePrefix) =>
        providerResourcePrefix.Contains("SqlServer", StringComparison.Ordinal) ? "mssql"
        : providerResourcePrefix.Contains("Postgres", StringComparison.Ordinal) ? "pg"
        : providerResourcePrefix.Contains("Sqlite", StringComparison.Ordinal) ? "sqlite"
        : throw new InvalidOperationException($"Cannot derive a dialect token from resource prefix '{providerResourcePrefix}'.");

    public static IEnumerable<(string LogicalPath, string Sql)> Enumerate(
        string dialectToken,
        bool includeVersionedMigrations = false,
        bool includeViews = false
    )
    {
        var runtime = typeof(SqlResourceCatalog).Assembly;
        var runtimePrefix = runtime.GetName().Name + ".";
        var suffix = "." + dialectToken + ".sql";
        foreach (
            var resource in runtime
                .GetManifestResourceNames()
                .Where(n => n.EndsWith(suffix, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
        )
        {
            var path = resource[runtimePrefix.Length..^suffix.Length].Replace('.', '/') + suffix;
            if (includeViews || !SqlResourceCatalog.IsView(Stem(path)))
            {
                yield return (path, ReadResource(runtime, resource));
            }
        }

        var assembly = Assembly.Load(ProviderAssemblyName(dialectToken));
        var prefix = assembly.GetName().Name + ".";

        foreach (var resource in assembly.GetManifestResourceNames().OrderBy(static n => n, StringComparer.Ordinal))
        {
            if (!resource.StartsWith(prefix, StringComparison.Ordinal) || !resource.EndsWith(".sql", StringComparison.Ordinal))
            {
                continue;
            }

            var tail = resource[prefix.Length..];
            var executable = tail.StartsWith("Sql.", StringComparison.Ordinal);
            var migration = tail.StartsWith("Schema.Migrations.M", StringComparison.Ordinal);
            if (!executable && !(includeVersionedMigrations && migration))
            {
                continue;
            }
            var path = LogicalPath(tail);
            if (!includeViews && SqlResourceCatalog.IsView(Stem(path)))
            {
                continue;
            }

            yield return (path, ReadResource(assembly, resource));
        }
    }

    public static IEnumerable<(string LogicalPath, string Sql)> EnumerateIncludingViews(string dialectToken) =>
        Enumerate(dialectToken, includeViews: true);

    /// <summary>The SQL of one dialect's file with this stem.</summary>
    public static string Read(string dialectToken, string stem) =>
        EnumerateIncludingViews(dialectToken).Single(r => Stem(r.LogicalPath) == stem).Sql;

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

    private static string ReadResource(Assembly assembly, string resource)
    {
        using var stream =
            assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Resource '{resource}' disappeared during enumeration.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string LogicalPath(string resourceTail)
    {
        const string migrationPrefix = "Schema.Migrations.";
        return resourceTail.StartsWith(migrationPrefix, StringComparison.Ordinal)
            ? "Schema/Migrations/" + resourceTail[migrationPrefix.Length..]
            : SqlLogicalPath.FromResourceTail(resourceTail);
    }
}

/// <summary>
/// Turns a dotted embedded-resource tail back into its <c>Sql/{Capability}/{Operation}.sql</c> path.
/// Directory separators and the dots inside a file name are indistinguishable once embedded, so the
/// rule is positional: strip <c>.sql</c> and any <c>.routine</c>/<c>.view</c> execution infix, treat
/// the last remaining segment as the file stem, and every segment before it as a directory.
/// </summary>
internal static class SqlLogicalPath
{
    public static string FromResourceTail(string resourceTail)
    {
        var body = resourceTail[..^".sql".Length];
        var infix = "";
        foreach (var candidate in (string[])[".routine", ".view"])
        {
            if (body.EndsWith(candidate, StringComparison.Ordinal))
            {
                infix = candidate;
                body = body[..^candidate.Length];
            }
        }

        var lastDot = body.LastIndexOf('.');
        return lastDot < 0 ? body + infix + ".sql" : body[..lastDot].Replace('.', '/') + "/" + body[(lastDot + 1)..] + infix + ".sql";
    }
}
