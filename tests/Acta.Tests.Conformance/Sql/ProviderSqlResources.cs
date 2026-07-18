using System.Reflection;

namespace Acta.Tests.Conformance.Sql;

/// <summary>Enumerates executable SQL directly from the selected provider assembly.</summary>
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
        var assembly = Assembly.Load(ProviderAssemblyName(dialectToken));
        var prefix = assembly.GetName().Name + ".";

        foreach (var resource in assembly.GetManifestResourceNames().OrderBy(static n => n, StringComparer.Ordinal))
        {
            if (!resource.StartsWith(prefix, StringComparison.Ordinal) || !resource.EndsWith(".sql", StringComparison.Ordinal))
            {
                continue;
            }

            var tail = resource[prefix.Length..];
            var executable =
                tail.StartsWith("Features.", StringComparison.Ordinal)
                || tail.StartsWith("Services.", StringComparison.Ordinal)
                || tail.StartsWith("Schema.Sql.", StringComparison.Ordinal);
            var migration = tail.StartsWith("Schema.Migrations.M", StringComparison.Ordinal);
            if (!executable && !(includeVersionedMigrations && migration))
            {
                continue;
            }
            if (!includeViews && tail.EndsWith(".view.sql", StringComparison.Ordinal))
            {
                continue;
            }

            using var stream =
                assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Provider resource '{resource}' disappeared during enumeration.");
            using var reader = new StreamReader(stream);
            yield return (LogicalPath(tail), reader.ReadToEnd());
        }
    }

    public static IEnumerable<(string LogicalPath, string Sql)> EnumerateIncludingViews(string dialectToken) =>
        Enumerate(dialectToken, includeViews: true);

    public static string ProviderAssemblyName(string dialectToken) =>
        dialectToken switch
        {
            "sqlite" => "Acta.Sqlite",
            "pg" => "Acta.Postgres",
            "mssql" => "Acta.SqlServer",
            _ => throw new InvalidOperationException($"No provider package known for dialect token '{dialectToken}'."),
        };

    private static string LogicalPath(string resourceTail)
    {
        const string migrationPrefix = "Schema.Migrations.";
        if (resourceTail.StartsWith(migrationPrefix, StringComparison.Ordinal))
        {
            return "Schema/Migrations/" + resourceTail[migrationPrefix.Length..];
        }

        var sqlMarker = resourceTail.IndexOf(".Sql.", StringComparison.Ordinal);
        if (sqlMarker >= 0)
        {
            var owner = resourceTail[..sqlMarker].Replace('.', '/');
            return owner + "/Sql/" + resourceTail[(sqlMarker + ".Sql.".Length)..];
        }

        var firstDot = resourceTail.IndexOf('.');
        return firstDot < 0 ? resourceTail : resourceTail[..firstDot] + "/" + resourceTail[(firstDot + 1)..];
    }
}
