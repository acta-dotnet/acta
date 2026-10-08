using System.Reflection;

namespace Acta.Tests.Conformance.Sql;

/// <summary>
/// Enumerates one dialect's executable SQL as the catalog sees it, the <c>X.{dialect}.sql</c> files
/// <c>Acta</c> embeds, and on request the provider's ordered migrations.
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

        if (!includeVersionedMigrations)
        {
            yield break;
        }

        var provider = Assembly.Load(ProviderAssemblyName(dialectToken));
        var migrations = provider.GetName().Name + ".Schema.Migrations.";
        foreach (
            var resource in provider
                .GetManifestResourceNames()
                .Where(n => n.StartsWith(migrations + "M", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
        )
        {
            yield return ("Schema/Migrations/" + resource[migrations.Length..], ReadResource(provider, resource));
        }
    }

    public static IEnumerable<(string LogicalPath, string Sql)> EnumerateIncludingViews(string dialectToken) =>
        Enumerate(dialectToken, includeViews: true);

    public static readonly string[] Dialects = ["pg", "mssql", "sqlite"];

    /// <summary>Every stem of one dialect, views and schema commands included.</summary>
    public static IEnumerable<string> Stems(string dialectToken) => EnumerateIncludingViews(dialectToken).Select(r => Stem(r.LogicalPath));

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
}
