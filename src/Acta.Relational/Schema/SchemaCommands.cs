using System.Data.Common;
using System.Globalization;
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
        cmd.CommandText = sql.Load("Sql/Schema/AcquireSchemaLock.sql");
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
        cmd.CommandText = sql.Load("Sql/Schema/EnsureMigrations.sql");
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Whether the migration-history ledger exists at all. The read-only preflight asks this first so
    /// an unprovisioned database is reported as unprovisioned rather than as whatever the provider's
    /// "no such table" error happens to be.
    /// </summary>
    public static async Task<bool> MigrationsTableExists(
        DbConnection conn,
        SchemaMigrationProviderHooks hooks,
        SqlResourceCatalog sql,
        CancellationToken ct
    )
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = hooks.CommandTimeoutSeconds;
        cmd.CommandText = sql.Load("Sql/Schema/MigrationsTableExists.sql");
        var found = await cmd.ExecuteScalarAsync(ct);
        return found is not null and not DBNull && Convert.ToInt64(found, CultureInfo.InvariantCulture) > 0;
    }

    // tx is nullable because the read-only preflight runs outside a transaction: it takes no schema
    // lock and writes nothing, so there is no boundary for it to join.
    public static async Task<IReadOnlyDictionary<int, string>> LoadAppliedVersions(
        DbConnection conn,
        DbTransaction? tx,
        SchemaMigrationProviderHooks hooks,
        SqlResourceCatalog sql,
        CancellationToken ct
    )
    {
        var applied = new Dictionary<int, string>();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = hooks.CommandTimeoutSeconds;
        cmd.CommandText = sql.Load("Sql/Schema/LoadAppliedVersions.sql");
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
        cmd.CommandText = sql.Load("Sql/Schema/DropSchema.sql");
        cmd.CommandTimeout = hooks.CommandTimeoutSeconds;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
