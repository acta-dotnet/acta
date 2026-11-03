using System.Data;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Acta.Postgres;

/// <summary>
/// Applies this package's migrations on PostgreSQL, each file as a single <c>NpgsqlCommand</c>.
/// </summary>
internal sealed class PostgresSchemaMigrator(SqlProviderOptions options, ILogger<PostgresSchemaMigrator>? log = null)
    : SchemaBootstrap(options, log)
{
    /// <summary>
    /// The Npgsql major this package is built and certified against. Bound to the <c>Npgsql</c>
    /// version pinned in <c>Directory.Packages.props</c>: <c>DriverMajorParityTests</c> fails the
    /// build if the two ever say different things, so bump them together and only after the suite has
    /// run against the new major.
    /// </summary>
    internal const int CertifiedDriverMajor = 10;

    internal const string DialectToken = "pg";
    private static readonly Assembly ProviderAssembly = typeof(PostgresSchemaMigrator).Assembly;

    protected override Assembly DriverAssembly => typeof(NpgsqlConnection).Assembly;

    protected override int CertifiedMajor => CertifiedDriverMajor;

    protected override Task<IReadOnlyList<string>> ApplyOnStartupAsync(string connectionString, string schemaName, CancellationToken ct) =>
        EnsureDatabaseAndApplyAsync(connectionString, schemaName, ct);

    protected override Task<IReadOnlyList<string>> ApplyOnDeployAsync(string connectionString, string schemaName, CancellationToken ct) =>
        ApplyToDatabaseAsync(connectionString, schemaName, ct);

    protected override Task PreflightOnStartupAsync(string connectionString, string schemaName, CancellationToken ct) =>
        PreflightAsync(connectionString, schemaName, ct);

    /// <summary>
    /// The deploy step's apply: opens the database the connection string names, never the maintenance database, and applies
    /// what it lacks. Behind <c>PostgresMigrations.ApplyAsync</c> and <c>jobs migrate</c>.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> ApplyToDatabaseAsync(string connectionString, string schema, CancellationToken ct)
    {
        RequireDatabase(connectionString);
        SqlIdentifierSyntax.ValidateBareIdentifier(schema, nameof(schema));

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        return await SchemaMigrations.ApplyAsync(conn, schema, DialectToken, ProviderAssembly, ct);
    }

    /// <summary>The database the connection string names, refused before anything opens when it names none.</summary>
    internal static string RequireDatabase(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var database = new NpgsqlConnectionStringBuilder(connectionString).Database;
        return string.IsNullOrWhiteSpace(database)
            ? throw new ArgumentException(
                "The connection string must name the database to install into (Database); without one Npgsql opens a database named after the user.",
                nameof(connectionString)
            )
            : database;
    }

    /// <summary>
    /// Dev convenience: connects to <c>postgres</c>, creates the target database if it is missing, then runs
    /// <see cref="ApplyToDatabaseAsync"/>. A deploy step applies to a database infrastructure created.
    /// </summary>
    public static async Task<IReadOnlyList<string>> EnsureDatabaseAndApplyAsync(
        string connectionString,
        string schemaName,
        CancellationToken ct
    )
    {
        var databaseName = RequireDatabase(connectionString);
        SqlIdentifierSyntax.ValidateBareIdentifier(schemaName, nameof(schemaName));
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

        return await ApplyToDatabaseAsync(connectionString, schemaName, ct);
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
