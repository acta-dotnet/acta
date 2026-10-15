using System.Data;
using System.Reflection;
using Npgsql;

namespace Acta.Postgres;

/// <summary>
/// Applies this package's migrations on PostgreSQL, each file as a single <c>NpgsqlCommand</c>.
/// </summary>
internal static class PostgresSchemaMigrator
{
    private const string DialectToken = "pg";
    private static readonly Assembly ProviderAssembly = typeof(PostgresSchemaMigrator).Assembly;

    public static async Task ApplyAsync(NpgsqlConnection connection, string schemaName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        SqlIdentifierSyntax.ValidateBareIdentifier(schemaName, nameof(schemaName));
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        await SchemaMigrations.ApplyAsync(connection, schemaName, DialectToken, ProviderAssembly, ct);
    }

    /// <summary>
    /// Dev convenience: connects to `postgres`, creates the target DB if missing, then ApplyAsync.
    /// Production deployments should create the DB in infrastructure and call ApplyAsync directly.
    /// </summary>
    public static async Task EnsureDatabaseAndApplyAsync(string connectionString, string schemaName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        SqlIdentifierSyntax.ValidateBareIdentifier(schemaName, nameof(schemaName));

        var targetBuilder = new NpgsqlConnectionStringBuilder(connectionString);
        var databaseName = targetBuilder.Database;
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw new InvalidOperationException("EnsureDatabaseAndApplyAsync requires the connection string to include a Database name.");
        }
        SqlIdentifierSyntax.ValidateDatabaseName(databaseName, nameof(databaseName));

        var maintenanceBuilder = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" };
        await using (var maintenance = new NpgsqlConnection(maintenanceBuilder.ConnectionString))
        {
            await maintenance.OpenAsync(ct);
            await using var exists = maintenance.CreateCommand();
            exists.CommandTimeout = SchemaMigrations.CommandTimeoutSeconds;
            exists.CommandText = "SELECT 1 FROM pg_database WHERE datname = @db";
            exists.Parameters.Add(new NpgsqlParameter("@db", NpgsqlTypes.NpgsqlDbType.Text) { Value = databaseName });
            if (await exists.ExecuteScalarAsync(ct) is null)
            {
                try
                {
                    await using var create = maintenance.CreateCommand();
                    create.CommandTimeout = SchemaMigrations.CommandTimeoutSeconds;
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

    /// <summary>
    /// Read-only migration-history preflight against the configured database. Connects to the target
    /// directly - it neither creates the database nor touches the maintenance connection, because a
    /// host that does not apply migrations has no business doing either.
    /// </summary>
    public static async Task PreflightAsync(string connectionString, string schemaName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        SqlIdentifierSyntax.ValidateBareIdentifier(schemaName, nameof(schemaName));

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await SchemaMigrations.PreflightAsync(conn, schemaName, DialectToken, ProviderAssembly, ct);
    }

    public static async Task ResetSchemaAsync(NpgsqlConnection connection, string schemaName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        SqlIdentifierSyntax.ValidateBareIdentifier(schemaName, nameof(schemaName));
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        await SchemaMigrations.ResetSchemaAsync(connection, schemaName, DialectToken, ProviderAssembly, ct);
    }

    /// <summary>
    /// Drops the schema and everything in it, applying nothing after. The drop half of
    /// <see cref="ResetSchemaAsync"/>, for a caller that wants the schema gone rather than fresh.
    /// </summary>
    public static async Task DropSchemaAsync(NpgsqlConnection connection, string schemaName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        SqlIdentifierSyntax.ValidateBareIdentifier(schemaName, nameof(schemaName));
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        await SchemaMigrations.DropSchemaAsync(connection, schemaName, DialectToken, ct);
    }
}
