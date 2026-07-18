using System.Data;
using Acta.Relational.Schema;
using Npgsql;

namespace Acta.Postgres;

/// <summary>
/// Applies <c>Mnnn_*.sql</c> migrations on PostgreSQL. PG scripts execute as a single
/// <c>NpgsqlCommand</c> (no <c>GO</c> splitter).
/// </summary>
public static class PostgresSchemaMigrator
{
    private static readonly SchemaMigrationProviderHooks Hooks = new(
        ProviderAssembly: typeof(PostgresSchemaMigrator).Assembly,
        DialectToken: "pg",
        SplitBatches: static script => [script]
    );

    public static async Task ApplyAsync(NpgsqlConnection connection, string schemaName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        IdentifierSyntax.ValidateBareIdentifier(schemaName, nameof(schemaName));
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        await SchemaMigrationRunner.ApplyAsync(connection, schemaName, Hooks, ct);
    }

    // Dev convenience: connects to `postgres`, creates the target DB if missing, then ApplyAsync.
    // Production deployments should create the DB in infrastructure and call ApplyAsync directly.
    public static async Task EnsureDatabaseAndApplyAsync(string connectionString, string schemaName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        IdentifierSyntax.ValidateBareIdentifier(schemaName, nameof(schemaName));

        var targetBuilder = new NpgsqlConnectionStringBuilder(connectionString);
        var databaseName = targetBuilder.Database;
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw new InvalidOperationException("EnsureDatabaseAndApplyAsync requires the connection string to include a Database name.");
        }
        IdentifierSyntax.ValidateDatabaseName(databaseName, nameof(databaseName));

        var maintenanceBuilder = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" };
        await using (var maintenance = new NpgsqlConnection(maintenanceBuilder.ConnectionString))
        {
            await maintenance.OpenAsync(ct);
            await using var exists = maintenance.CreateCommand();
            exists.CommandTimeout = Hooks.CommandTimeoutSeconds;
            exists.CommandText = "SELECT 1 FROM pg_database WHERE datname = @db";
            exists.Parameters.Add(new NpgsqlParameter("@db", NpgsqlTypes.NpgsqlDbType.Text) { Value = databaseName });
            if (await exists.ExecuteScalarAsync(ct) is null)
            {
                try
                {
                    await using var create = maintenance.CreateCommand();
                    create.CommandTimeout = Hooks.CommandTimeoutSeconds;
                    // CREATE DATABASE doesn't accept parameters; safe because databaseName is validated
                    // by ValidateDatabaseName (no quote characters) before interpolation.
                    create.CommandText = $"CREATE DATABASE \"{databaseName}\"";
                    await create.ExecuteNonQueryAsync(ct);
                }
                // A concurrent initializer (parallel test assemblies share one target database) can
                // create it between our check and create. PG reports the loser of the race as either
                // DuplicateDatabase or, when both pass the existence check and collide on the catalog
                // insert, a UniqueViolation on pg_database_datname_index. Both mean it now exists.
                catch (PostgresException ex)
                    when (ex.SqlState == PostgresErrorCodes.DuplicateDatabase || ex.SqlState == PostgresErrorCodes.UniqueViolation) { }
            }
        }

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await ApplyAsync(conn, schemaName, ct);
    }

    public static async Task ResetSchemaAsync(NpgsqlConnection connection, string schemaName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        IdentifierSyntax.ValidateBareIdentifier(schemaName, nameof(schemaName));
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        await SchemaMigrationRunner.ResetSchemaAsync(connection, schemaName, Hooks, ct);
    }
}
