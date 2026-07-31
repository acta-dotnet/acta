using System.Data;
using System.Text.RegularExpressions;
using Acta.Relational.Schema;
using Microsoft.Data.SqlClient;

namespace Acta.SqlServer.Schema;

/// <summary>
/// Applies <c>Mnnn_*.sql</c> migrations on SQL Server. The prelude turns
/// <c>QUOTED_IDENTIFIER ON; ANSI_NULLS ON;</c> on (M001 has filtered indexes that require it;
/// the option is session-scoped, so the runner sets it on every entry).
/// </summary>
internal static partial class SqlServerSchemaMigrator
{
    private static readonly SchemaMigrationProviderHooks Hooks = new(
        ProviderAssembly: typeof(SqlServerSchemaMigrator).Assembly,
        DialectToken: "mssql",
        SplitBatches: SplitOnGo,
        PreludeSql: "SET QUOTED_IDENTIFIER ON; SET ANSI_NULLS ON;",
        ObjectDefinitionSql: "SELECT OBJECT_DEFINITION(OBJECT_ID(@p_name));"
    );

    public static async Task ApplyAsync(SqlConnection connection, string schemaName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        IdentifierSyntax.ValidateBareIdentifier(schemaName, nameof(schemaName));
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        await SchemaMigrationRunner.ApplyAsync(connection, schemaName, Hooks, ct);
    }

    // Dev convenience: connects to master, creates the DB if missing, then calls ApplyAsync.
    // Production deployments should create the DB in infrastructure and call ApplyAsync directly.
    public static async Task EnsureDatabaseAndApplyAsync(string connectionString, string schemaName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        IdentifierSyntax.ValidateBareIdentifier(schemaName, nameof(schemaName));

        var targetBuilder = new SqlConnectionStringBuilder(connectionString);
        var databaseName = targetBuilder.InitialCatalog;
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw new InvalidOperationException(
                "EnsureDatabaseAndApplyAsync requires the connection string to include an Initial Catalog (target database name)."
            );
        }
        IdentifierSyntax.ValidateDatabaseName(databaseName, nameof(databaseName));

        var masterBuilder = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" };
        await using (var master = new SqlConnection(masterBuilder.ConnectionString))
        {
            await master.OpenAsync(ct);
            await using (var cmd = master.CreateCommand())
            {
                // databaseName is interpolated into both a string-literal (N'...') and an identifier
                // ([...]) context; ValidateDatabaseName rejects ' and ], so neither can break out.
                cmd.CommandText = $"IF DB_ID(N'{databaseName}') IS NULL CREATE DATABASE [{databaseName}];";
                cmd.CommandTimeout = Hooks.CommandTimeoutSeconds;
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

            // READ_COMMITTED_SNAPSHOT ON gives SQL Server row-versioned reads (parity with Postgres
            // MVCC): readers no longer take shared locks. It does NOT stop an UPDATE from locking the
            // rows its predicate scans, so writer-side deadlock-avoidance still lives in the routines
            // (e.g. the register_scheduled_jobs orphan-sweep seeks by clustered id); RCSI's payoff there is that
            // the id-collect step reads lock-free. Idempotent; WITH ROLLBACK IMMEDIATE lets an existing
            // DB flip without waiting on open sessions (we are on master, so it never targets our own
            // connection). Infra-provisioned production DBs should set this too.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await using var rcsi = master.CreateCommand();
                    rcsi.CommandTimeout = Hooks.CommandTimeoutSeconds;
                    rcsi.CommandText = $"""
                        IF (SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = N'{databaseName}') = 0
                            ALTER DATABASE [{databaseName}] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
                        """;
                    await rcsi.ExecuteNonQueryAsync(ct);
                    break;
                }
                catch (SqlException ex) when (attempt < 60 && IsTransientBootstrapNumber(ex.Number))
                {
                    // Transient bootstrap contention: 5061 (a lock could not be placed - a concurrent
                    // bootstrapper is creating the DB or flipping RCSI itself), or a severe error that
                    // killed this session while the freshly-created DB was still settling. The IF guard
                    // makes the retry a no-op once any caller's ALTER lands; reconnect first because a
                    // killed session cannot run further commands.
                    await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
                    await master.CloseAsync();
                    await master.OpenAsync(ct);
                }
            }
        }

        // RCSI ON WITH ROLLBACK IMMEDIATE bounces the database offline, and the ALTER returns before
        // the restart settles - polling sys.databases is unreliable because state can still read
        // ONLINE in the gap before the restart begins. Retry the real connection (open + a probe
        // query) until the database accepts it, so the migration never runs mid-restart.
        await using var conn = await OpenWhenReadyAsync(connectionString, ct);
        await ApplyAsync(conn, schemaName, ct);
    }

    private static async Task<SqlConnection> OpenWhenReadyAsync(string connectionString, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var conn = new SqlConnection(connectionString);
            try
            {
                await conn.OpenAsync(ct);
                await using (var probe = conn.CreateCommand())
                {
                    probe.CommandTimeout = Hooks.CommandTimeoutSeconds;
                    probe.CommandText = "SELECT 1;";
                    await probe.ExecuteScalarAsync(ct);
                }
                return conn;
            }
            // A database mid-restart rejects logins (18456 "Infrastructure error") or kills the
            // command ("A severe error occurred"); both surface as SqlException. Retry, bounded.
            catch (SqlException ex) when (attempt < 60 && IsTransientBootstrapNumber(ex.Number))
            {
                await conn.DisposeAsync();
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            }
            catch
            {
                await conn.DisposeAsync();
                throw;
            }
        }
    }

    public static async Task ResetSchemaAsync(SqlConnection connection, string schemaName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        IdentifierSyntax.ValidateBareIdentifier(schemaName, nameof(schemaName));
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        await SchemaMigrationRunner.ResetSchemaAsync(connection, schemaName, Hooks, ct);
    }

    // The bounded bootstrap retries admit only documented transient conditions; a permanent
    // configuration error (missing permission, syntax, definitively bad credentials) surfaces on the
    // first attempt instead of burning the whole 30s budget. 1205 deadlock victim; 1807/5061
    // concurrent CREATE/ALTER DATABASE contention; 4060 database not yet openable (mid-create or
    // mid-restart); 18456 login rejected while the freshly-bounced database settles (credentials
    // were already proven against master before either retry loop runs, so this cannot mask a wrong
    // password); -2 client timeout; 0/64/233/10053/10054/10060 connection killed or reset.
    internal static bool IsTransientBootstrapNumber(int number) =>
        number is 1205 or 1807 or 5061 or 4060 or 18456 or -2 or 0 or 64 or 233 or 10053 or 10054 or 10060;

    // Splits on a line containing only `GO`. Don't put a bare `GO` inside string literals.
    internal static IEnumerable<string> SplitOnGo(string script) => GoSeparatorRegex().Split(script);

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoSeparatorRegex();
}
