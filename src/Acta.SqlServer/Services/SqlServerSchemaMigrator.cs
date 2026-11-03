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
    /// READ_COMMITTED_SNAPSHOT on only in a database that holds no objects yet. Acta runs with it on or off, so on any other
    /// database the setting stays its owner's.
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
            -- Dynamic: a system database refuses ALTER DATABASE CURRENT when the batch compiles, before the refusal above runs.
            EXEC (N'ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE');
        """;

    private const string DialectToken = "mssql";

    // A proven login waits out a database another installer is still creating; an unproven one waits a few seconds for one
    // another installer is switching. The step itself restarts a few times at most.
    private const int ProvenOpenAttempts = 60;
    private const int UnprovenOpenAttempts = 10;
    private const int InstallAttempts = 5;

    private static readonly Assembly ProviderAssembly = typeof(SqlServerSchemaMigrator).Assembly;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    protected override Assembly DriverAssembly => typeof(SqlConnection).Assembly;

    protected override int CertifiedMajor => CertifiedDriverMajor;

    protected override Task ApplyOnStartupAsync(string connectionString, string schemaName, CancellationToken ct) =>
        EnsureDatabaseAndApplyAsync(connectionString, schemaName, ct);

    protected override Task PreflightOnStartupAsync(string connectionString, string schemaName, CancellationToken ct) =>
        PreflightAsync(connectionString, schemaName, ct);

    public static async Task<IReadOnlyList<string>> ApplyAsync(SqlConnection connection, string schemaName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        SqlIdentifierSyntax.ValidateBareIdentifier(schemaName, nameof(schemaName));
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        return await SchemaMigrations.ApplyAsync(connection, schemaName, DialectToken, ProviderAssembly, ct);
    }

    /// <summary>
    /// Dev convenience: connects to master, creates the database if it is missing, then runs <see cref="ApplyToDatabaseAsync"/>.
    /// Production creates the database in infrastructure and applies to it from a deploy step.
    /// </summary>
    public static async Task<IReadOnlyList<string>> EnsureDatabaseAndApplyAsync(
        string connectionString,
        string schemaName,
        CancellationToken ct
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        SqlIdentifierSyntax.ValidateBareIdentifier(schemaName, nameof(schemaName));

        var targetBuilder = new SqlConnectionStringBuilder(connectionString);
        var databaseName = targetBuilder.InitialCatalog;
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw new InvalidOperationException(
                "EnsureDatabaseAndApplyAsync requires the connection string to include an Initial Catalog (target database name)."
            );
        }
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
    /// nothing but the target, so it never needs master.
    /// </summary>
    internal static Task<IReadOnlyList<string>> ApplyToDatabaseAsync(string connectionString, string schemaName, CancellationToken ct) =>
        ApplyToDatabaseAsync(connectionString, schemaName, loginProven: false, ct);

    private static async Task<IReadOnlyList<string>> ApplyToDatabaseAsync(
        string connectionString,
        string schemaName,
        bool loginProven,
        CancellationToken ct
    )
    {
        // Before anything opens: the guard can switch the database's setting and end its other sessions.
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        SqlIdentifierSyntax.ValidateBareIdentifier(schemaName, nameof(schemaName));
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.InitialCatalog))
        {
            throw new ArgumentException(
                "The connection string must name the database to install into (Initial Catalog); without one SQL Server opens the login's default database.",
                nameof(connectionString)
            );
        }

        // Unpooled: after a rejected login the pool answers every open with the cached error for its blocking period, which
        // would outlast the retries, and a pooled session another installer's ROLLBACK IMMEDIATE ended must not come back.
        builder.Pooling = false;
        var installConnection = builder.ConnectionString;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = await OpenAsync(installConnection, loginProven, ct);
                loginProven = true;
                await using (var guard = conn.CreateCommand())
                {
                    guard.CommandTimeout = SchemaMigrations.CommandTimeoutSeconds;
                    guard.CommandText = InstallGuard;
                    await guard.ExecuteNonQueryAsync(ct);
                }

                // The same connection: the session that ran the ALTER keeps the database, and the published script runs
                // every batch after it the same way.
                return await ApplyAsync(conn, schemaName, ct);
            }
            catch (SqlException ex)
                when (attempt < InstallAttempts && IsConcurrentInstall(ex.Errors.Cast<SqlError>().Select(e => e.Number)))
            {
                // The apply rolled back, and the guard finds the setting already on when the step runs again.
                await Task.Delay(RetryDelay, ct);
            }
        }
    }

    private static async Task<SqlConnection> OpenAsync(string connectionString, bool loginProven, CancellationToken ct)
    {
        var attempts = loginProven ? ProvenOpenAttempts : UnprovenOpenAttempts;
        for (var attempt = 1; ; attempt++)
        {
            var conn = new SqlConnection(connectionString);
            try
            {
                await conn.OpenAsync(ct);
                return conn;
            }
            catch (SqlException ex) when (attempt < attempts && RetriesOpen(ex.Number, loginProven))
            {
                await conn.DisposeAsync();
                await Task.Delay(RetryDelay, ct);
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
    /// Whether an open retries: 4060, a database another installer is still creating or switching; 18456 only once the login
    /// is proven. Before that an 18456 is most likely a wrong password, which must fail at once and must not count a real
    /// failed login per attempt toward a lockout.
    /// </summary>
    internal static bool RetriesOpen(int number, bool loginProven) => number == 4060 || (loginProven && number == 18456);

    /// <summary>
    /// Whether a failed step was another installer's guard colliding with it, which starts the step again: 596 session killed,
    /// and 0, 64, 233, 10053, 10054, and 10060 connection reset, by its ROLLBACK IMMEDIATE; 5061 its ALTER holding the
    /// database. Every error counts, since a killed ALTER reports 5069 ahead of the 596. A timeout or a deadlock fails the
    /// step, so a migration batch is never repeated behind a blocking workload.
    /// </summary>
    internal static bool IsConcurrentInstall(IEnumerable<int> errorNumbers) =>
        errorNumbers.Any(number => number is 596 or 5061 or 0 or 64 or 233 or 10053 or 10054 or 10060);
}
