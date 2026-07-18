using System.Data.Common;
using Acta.Relational.Resources;

namespace Acta.Relational.Schema;

/// <summary>
/// Installs current provider-owned operator views and routines after pending migrations, inside the
/// same transaction and migration lock. Durable DDL stays in versioned migrations; idempotent object
/// bodies are reapplied at bootstrap, with SQL Server's byte-equivalent definitions skipped.
/// </summary>
internal static class SqlObjectInstaller
{
    public static async Task Run(
        DbConnection conn,
        DbTransaction tx,
        string schemaName,
        SchemaMigrationProviderHooks hooks,
        SqlResourceCatalog sql,
        CancellationToken ct
    )
    {
        foreach (var (qualifiedName, body) in DesiredObjects(sql, schemaName, hooks))
        {
            var batches = hooks.SplitBatches(body).Select(b => b.Trim()).Where(b => b.Length > 0).ToList();

            if (qualifiedName is not null && hooks.ObjectDefinitionSql is not null && batches.Count == 1)
            {
                var current = await CurrentDefinition(conn, tx, hooks, qualifiedName, ct);
                if (current is not null && string.Equals(Comparable(current), Comparable(batches[0]), StringComparison.Ordinal))
                {
                    continue;
                }
            }

            foreach (var batch in batches)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = batch;
                cmd.CommandTimeout = hooks.CommandTimeoutSeconds;
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
    }

    private static IEnumerable<(string? QualifiedName, string Body)> DesiredObjects(
        SqlResourceCatalog sql,
        string schemaName,
        SchemaMigrationProviderHooks hooks
    )
    {
        foreach (var view in ProviderViews(sql, schemaName, hooks.DialectToken))
        {
            yield return view;
        }

        foreach (var (name, body) in sql.Routines())
        {
            yield return ($"{schemaName}.{name}", body);
        }
    }

    private static IEnumerable<(string? QualifiedName, string Body)> ProviderViews(
        SqlResourceCatalog sql,
        string schemaName,
        string dialectToken
    )
    {
        foreach (var (name, body) in sql.Views())
        {
            var select = body.Trim();
            var qualifiedName = $"{schemaName}.{name}";

            switch (dialectToken)
            {
                case "mssql":
                    yield return (qualifiedName, $"CREATE OR ALTER VIEW {qualifiedName} AS\n{select}");
                    break;
                case "pg":
                case "sqlite":
                    yield return (null, $"DROP VIEW IF EXISTS {qualifiedName};");
                    yield return (qualifiedName, $"CREATE VIEW {qualifiedName} AS\n{select};");
                    break;
                default:
                    throw new InvalidOperationException($"Operator views are not mapped for dialect '{dialectToken}'.");
            }
        }
    }

    private static string Comparable(string sql) =>
        string.Join(
            ' ',
            sql.Replace("CREATE OR ALTER ", "CREATE ", StringComparison.Ordinal)
                .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        );

    private static async Task<string?> CurrentDefinition(
        DbConnection conn,
        DbTransaction tx,
        SchemaMigrationProviderHooks hooks,
        string qualifiedName,
        CancellationToken ct
    )
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = hooks.CommandTimeoutSeconds;
        cmd.CommandText = hooks.ObjectDefinitionSql!;
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = "@p_name";
        parameter.Value = qualifiedName;
        cmd.Parameters.Add(parameter);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is string definition ? definition : null;
    }
}
