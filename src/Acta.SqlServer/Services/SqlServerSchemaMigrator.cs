using System.Data;
using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Acta.SqlServer;

/// <summary>
/// Applies this package's migrations on SQL Server, each file split into its <c>GO</c> batches.
/// </summary>
internal sealed class SqlServerSchemaMigrator(SqlProviderOptions options, ILogger<SqlServerSchemaMigrator>? log = null)
    : SchemaBootstrap(options, log)
{
    /// <summary>
    /// The Microsoft.Data.SqlClient major this package is built and certified against. Bound to the
    /// <c>Microsoft.Data.SqlClient</c> version pinned in <c>Directory.Packages.props</c>:
    /// <c>DriverMajorParityTests</c> fails the build if the two ever say different things, so bump
    /// them together and only after the suite has run against the new major.
    /// </summary>
    internal const int CertifiedDriverMajor = 7;

    /// <summary>
    /// The first batch of every SQL Server install, the published script's included: it refuses a system database and
    /// changes no database setting.
    /// </summary>
    internal const string InstallGuard = """
        IF DB_NAME() IN (N'master', N'tempdb', N'model', N'msdb')
        BEGIN
            RAISERROR(N'Acta installs into a database of its own, never master, tempdb, model or msdb.', 16, 1);
            SET NOEXEC ON;
        END
        """;

    internal const string DialectToken = "mssql";

    private const int OpenAttempts = 60;

    private static readonly Assembly ProviderAssembly = typeof(SqlServerSchemaMigrator).Assembly;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    protected override Assembly DriverAssembly => typeof(SqlConnection).Assembly;

    protected override int CertifiedMajor => CertifiedDriverMajor;

    protected override Task<IReadOnlyList<string>> ApplyOnStartupAsync(string connectionString, string schemaName, CancellationToken ct) =>
        EnsureDatabaseAndApplyAsync(connectionString, schemaName, ct);

    protected override Task<IReadOnlyList<string>> ApplyOnDeployAsync(string connectionString, string schemaName, CancellationToken ct) =>
        ApplyToDatabaseAsync(connectionString, schemaName, ct);

    protected override Task PreflightOnStartupAsync(string connectionString, string schemaName, CancellationToken ct) =>
        PreflightAsync(connectionString, schemaName, ct);

    /// <summary>
    /// Dev convenience: connects to master, creates the database if it is missing, then runs <see cref="ApplyToDatabaseAsync"/>.
    /// A database it creates gets READ_COMMITTED_SNAPSHOT at once, as EF Core's database creator does; an existing one keeps
    /// its setting. A deploy step applies to a database infrastructure created.
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

        var masterBuilder = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" };
        await using (var master = new SqlConnection(masterBuilder.ConnectionString))
        {
            await master.OpenAsync(ct);
            await using var cmd = master.CreateCommand();
            // databaseName is interpolated into string literals (N'...') and identifiers ([...]); ValidateDatabaseName
            // rejects ' and ], so none can break out. The switch follows CREATE DATABASE in the same batch, sooner than
            // another installer that sees the new database can log in to it, and XACT_ABORT ends the batch at a lost race's
            // 1801 before it switches another installer's database.
            cmd.CommandText = $"""
                SET XACT_ABORT ON;
                IF DB_ID(N'{databaseName}') IS NULL
                BEGIN
                    CREATE DATABASE [{databaseName}];
                    IF SERVERPROPERTY('EngineEdition') <> 5 -- Azure SQL Database creates it with the option on
                        EXEC (N'ALTER DATABASE [{databaseName}] SET READ_COMMITTED_SNAPSHOT ON');
                END
                """;
            cmd.CommandTimeout = SchemaMigrations.CommandTimeoutSeconds;
            try
            {
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (SqlException ex) when (ex.Number == 1801)
            {
                // 1801: database already exists - a concurrent bootstrapper won the IF-free
                // window between DB_ID and CREATE DATABASE. The database is there; proceed.
            }
        }

        // master proved the login, so a refused one here is another installer still creating or switching the database: wait it
        // out. Both numbers are login errors, so nothing has run when they fire.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ApplyToDatabaseAsync(connectionString, schemaName, ct);
            }
            catch (SqlException ex) when (attempt < OpenAttempts && ex.Number is 4060 or 18456)
            {
                await Task.Delay(RetryDelay, ct);
            }
        }
    }

    /// <summary>The database the connection string names, refused before anything opens when it names none.</summary>
    internal static string RequireDatabase(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var database = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        return string.IsNullOrWhiteSpace(database)
            ? throw new ArgumentException(
                "The connection string must name the database to install into (Initial Catalog); without one SQL Server opens the login's default database.",
                nameof(connectionString)
            )
            : database;
    }

    /// <summary>
    /// The target-database step every SQL Server apply runs: open the database, run <see cref="InstallGuard"/>, apply. It opens
    /// nothing but the target, so it never needs master. Behind <c>SqlServerMigrations.ApplyAsync</c> and <c>jobs migrate</c>.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> ApplyToDatabaseAsync(string connectionString, string schema, CancellationToken ct)
    {
        // Before anything opens, so a refused argument changes nothing.
        RequireDatabase(connectionString);
        SqlIdentifierSyntax.ValidateBareIdentifier(schema, nameof(schema));

        // Unpooled: after a rejected login the pool answers every open with the cached error for its blocking period, which
        // would outlast the development path's retry.
        await using var conn = new SqlConnection(new SqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await conn.OpenAsync(ct);
        await using (var guard = conn.CreateCommand())
        {
            guard.CommandTimeout = SchemaMigrations.CommandTimeoutSeconds;
            guard.CommandText = InstallGuard;
            await guard.ExecuteNonQueryAsync(ct);
        }

        return await SchemaMigrations.ApplyAsync(conn, schema, DialectToken, ProviderAssembly, ct);
    }

    /// <summary>
    /// Read-only migration-history preflight against the configured database. Connects to the target
    /// catalog directly - no master connection, no CREATE DATABASE - because a host that does not
    /// apply migrations has no business doing either.
    /// </summary>
    public static async Task PreflightAsync(string connectionString, string schemaName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        SqlIdentifierSyntax.ValidateBareIdentifier(schemaName, nameof(schemaName));

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await SchemaMigrations.PreflightAsync(conn, schemaName, DialectToken, ProviderAssembly, ct);
    }

    public static async Task ResetSchemaAsync(SqlConnection connection, string schemaName, CancellationToken ct)
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
    public static async Task DropSchemaAsync(SqlConnection connection, string schemaName, CancellationToken ct)
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
