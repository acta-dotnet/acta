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
    /// The first batch of every SQL Server install, the published script's included: it refuses a system database and turns
    /// READ_COMMITTED_SNAPSHOT on only in a database that holds no objects yet. The switch never ends another session, and a
    /// switch the server refuses leaves the setting as it is: Acta runs with it on or off.
    /// </summary>
    internal const string InstallGuard = """
        IF DB_NAME() IN (N'master', N'tempdb', N'model', N'msdb')
        BEGIN
            RAISERROR(N'Acta installs into a database of its own, never master, tempdb, model or msdb.', 16, 1);
            SET NOEXEC ON;
        END
        IF (SELECT is_read_committed_snapshot_on FROM sys.databases WHERE database_id = DB_ID()) = 0
           AND NOT EXISTS (SELECT 1 FROM sys.objects WHERE is_ms_shipped = 0)
           AND HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'ALTER') = 1
           AND SERVERPROPERTY('EngineEdition') <> 5 -- Azure SQL Database allows this change only from master
        BEGIN
            -- One installer switches and the others go on without waiting: the switch waits for their transactions, so a
            -- session waiting for it here would deadlock with it.
            DECLARE @switch_lock INT;
            EXEC @switch_lock = sp_getapplock N'acta-rcsi-switch', 'Exclusive', 'Session', 0;
            IF @switch_lock >= 0
            BEGIN
                IF (SELECT is_read_committed_snapshot_on FROM sys.databases WHERE database_id = DB_ID()) = 0
                BEGIN
                    BEGIN TRY
                        -- Dynamic: a system database refuses ALTER DATABASE CURRENT at compile time, before the refusal above runs.
                        EXEC (N'ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH NO_WAIT');
                    END TRY
                    BEGIN CATCH
                    END CATCH
                END
                EXEC sp_releaseapplock N'acta-rcsi-switch', 'Session';
            END
        END
        """;

    internal const string DialectToken = "mssql";

    // A proven login waits out a database another installer is still creating. An unproven one tries once more after two
    // seconds, since a login can be refused for a moment while another installer's switch completes with the same 18456 a
    // wrong password gets, so a wrong password costs two failed logins a run instead of one.
    private const int ProvenOpenAttempts = 60;
    private const int UnprovenOpenAttempts = 2;

    private static readonly Assembly ProviderAssembly = typeof(SqlServerSchemaMigrator).Assembly;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan UnprovenOpenDelay = TimeSpan.FromSeconds(2);

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
    /// A deploy step applies to a database infrastructure created.
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
            // databaseName is interpolated into both a string-literal (N'...') and an identifier
            // ([...]) context; ValidateDatabaseName rejects ' and ], so neither can break out.
            cmd.CommandText = $"IF DB_ID(N'{databaseName}') IS NULL CREATE DATABASE [{databaseName}];";
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

        // master proved the login, so the first open of the target may wait out a database another bootstrapper is still
        // creating or switching.
        return await ApplyToDatabaseAsync(connectionString, schemaName, loginProven: true, ct);
    }

    /// <summary>
    /// The target-database step every SQL Server apply runs: open the database, run <see cref="InstallGuard"/>, apply. It opens
    /// nothing but the target, so it never needs master. Behind <c>SqlServerMigrations.ApplyAsync</c> and <c>jobs migrate</c>.
    /// </summary>
    internal static Task<IReadOnlyList<string>> ApplyToDatabaseAsync(string connectionString, string schema, CancellationToken ct) =>
        ApplyToDatabaseAsync(connectionString, schema, loginProven: false, ct);

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

    private static async Task<IReadOnlyList<string>> ApplyToDatabaseAsync(
        string connectionString,
        string schema,
        bool loginProven,
        CancellationToken ct
    )
    {
        // Before anything opens, so a refused argument changes nothing.
        RequireDatabase(connectionString);
        SqlIdentifierSyntax.ValidateBareIdentifier(schema, nameof(schema));

        // Unpooled: after a rejected login the pool answers every open with the cached error for its blocking period, which
        // would outlast the open's retry.
        var installConnection = new SqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
        await using var conn = await OpenAsync(installConnection, loginProven, ct);
        await using (var guard = conn.CreateCommand())
        {
            guard.CommandTimeout = SchemaMigrations.CommandTimeoutSeconds;
            guard.CommandText = InstallGuard;
            await guard.ExecuteNonQueryAsync(ct);
        }

        // The same connection: the session that ran the ALTER keeps the database, and the published script runs every batch
        // after it the same way.
        return await SchemaMigrations.ApplyAsync(conn, schema, DialectToken, ProviderAssembly, ct);
    }

    private static async Task<SqlConnection> OpenAsync(string connectionString, bool loginProven, CancellationToken ct)
    {
        var (attempts, delay) = loginProven ? (ProvenOpenAttempts, RetryDelay) : (UnprovenOpenAttempts, UnprovenOpenDelay);
        for (var attempt = 1; ; attempt++)
        {
            var conn = new SqlConnection(connectionString);
            try
            {
                await conn.OpenAsync(ct);
                return conn;
            }
            catch (SqlException ex) when (attempt < attempts && RetriesOpen(ex.Number))
            {
                await conn.DisposeAsync();
                await Task.Delay(delay, ct);
            }
            catch
            {
                await conn.DisposeAsync();
                throw;
            }
        }
    }

    /// <summary>
    /// Read-only migration-history preflight against the configured database. Connects to the target
    /// catalog directly - no master connection, no CREATE DATABASE, no RCSI flip - because a host
    /// that does not apply migrations has no business doing any of those.
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

    /// <summary>
    /// Whether an open retries: 4060 or 18456, the logins a database another installer is creating or switching refuses for
    /// a moment. How often depends on whether the login is proven; any other error surfaces at once.
    /// </summary>
    internal static bool RetriesOpen(int number) => number is 4060 or 18456;
}
