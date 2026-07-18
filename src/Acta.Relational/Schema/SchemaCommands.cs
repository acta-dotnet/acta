using System.Data.Common;
using Acta.Relational.Resources;

namespace Acta.Relational.Schema;

/// <summary>Mechanical execution of the provider-owned schema-management command set.</summary>
internal static class SchemaCommands
{
    public static async Task AcquireLock(
        DbConnection conn,
        DbTransaction tx,
        string schemaName,
        SchemaMigrationProviderHooks hooks,
        SqlResourceCatalog sql,
        CancellationToken ct
    )
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = hooks.CommandTimeoutSeconds;
        cmd.CommandText = sql.Load("Schema/Sql/AcquireSchemaLock.sql");
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = "@p_key";
        parameter.Value = $"acta-migrations-{schemaName}";
        cmd.Parameters.Add(parameter);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task EnsureMigrations(
        DbConnection conn,
        DbTransaction tx,
        SchemaMigrationProviderHooks hooks,
        SqlResourceCatalog sql,
        CancellationToken ct
    )
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = hooks.CommandTimeoutSeconds;
        cmd.CommandText = sql.Load("Schema/Sql/EnsureMigrations.sql");
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task<IReadOnlyDictionary<int, string>> LoadAppliedVersions(
        DbConnection conn,
        DbTransaction tx,
        SchemaMigrationProviderHooks hooks,
        SqlResourceCatalog sql,
        CancellationToken ct
    )
    {
        var applied = new Dictionary<int, string>();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = hooks.CommandTimeoutSeconds;
        cmd.CommandText = sql.Load("Schema/Sql/LoadAppliedVersions.sql");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            applied.Add(reader.GetInt32(0), reader.GetString(1));
        }

        return applied;
    }

    public static async Task DropSchema(DbConnection conn, SchemaMigrationProviderHooks hooks, SqlResourceCatalog sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql.Load("Schema/Sql/DropSchema.sql");
        cmd.CommandTimeout = hooks.CommandTimeoutSeconds;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
