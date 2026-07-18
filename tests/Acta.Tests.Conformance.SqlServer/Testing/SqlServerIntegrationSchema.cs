using System.Data;
using Acta.SqlServer;
using Acta.Tests.Conformance.Testing;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Acta.Tests.Conformance.SqlServer.Testing;

/// <summary>
/// SQL Server-backed handle to the shared <c>acta_test</c> schema. Delegates migration apply to
/// <see cref="SqlServerSchemaMigrator"/> and upserts the test assembly's framework
/// <c>namespaces</c> row on first touch.
/// </summary>
/// <remarks>
/// The first <see cref="CreateAsync"/> call ensures the <c>acta_test</c> schema exists with M001
/// applied, then idempotently inserts the framework namespace row; later calls reuse the successful
/// connection string. <see cref="DisposeAsync"/> is a no-op so rows persist for inspection.
/// </remarks>
public sealed class SqlServerIntegrationSchema : IIntegrationSchema
{
    // Unique per test run: the append-only acta_test schema keeps rows across runs, so a fixed name
    // would be re-touched every run. A per-run name plus the race-safe ensure-exists below keep that
    // collision-free even after an explicit schema reset.
    private static readonly string FrameworkNamespaceName = "acta-tests-" + Guid.NewGuid().ToString("N");

    // xUnit runs test classes in parallel. Serialize cold bootstrap inside this process so a missing
    // acta-test database is created/applied once; cache only success so transient SQL Server startup
    // failures can retry on the next first-touch.
    private static readonly SemaphoreSlim s_bootstrapGate = new(1, 1);
    private static string? s_bootstrappedConnectionString;

    private SqlServerIntegrationSchema(string connectionString)
    {
        ConnectionString = connectionString;
    }

    public string SchemaName => IntegrationConfig.TestSchemaName;

    public string ConnectionString { get; }

    public static async ValueTask<IIntegrationSchema> CreateAsync()
    {
        if (s_bootstrappedConnectionString is { } cached)
        {
            return new SqlServerIntegrationSchema(cached);
        }

        await s_bootstrapGate.WaitAsync();
        try
        {
            if (s_bootstrappedConnectionString is null)
            {
                s_bootstrappedConnectionString = await BootstrapAsync();
            }

            return new SqlServerIntegrationSchema(s_bootstrappedConnectionString);
        }
        finally
        {
            s_bootstrapGate.Release();
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async Task<string> BootstrapAsync()
    {
        var adminConn = IntegrationConfig.SqlServerConnectionString;
        if (string.IsNullOrWhiteSpace(adminConn))
        {
            Assert.Skip("SQL Server integration tests require ACTA_TEST_MSSQL.");
        }

        var builder = new SqlConnectionStringBuilder(adminConn) { TrustServerCertificate = true, ConnectTimeout = 15 };
        var connectionString = builder.ConnectionString;

        EnsureTestTarget(builder.InitialCatalog, IntegrationConfig.TestSchemaName);

        // Ensure the database exists with READ_COMMITTED_SNAPSHOT ON (Postgres-MVCC parity, so the
        // parallel catalog bootstrap on the shared schema doesn't deadlock under pessimistic key locks)
        // and M001 applied. EnsureDatabaseAndApplyAsync runs the QUOTED_IDENTIFIER prelude internally.
        await SqlServerSchemaMigrator.EnsureDatabaseAndApplyAsync(
            connectionString,
            IntegrationConfig.TestSchemaName,
            CancellationToken.None
        );

        await using (var conn = new SqlConnection(connectionString))
        {
            await conn.OpenAsync();
            await UpsertFrameworkNamespaceAsync(conn);
        }

        return connectionString;
    }

    private static async Task UpsertFrameworkNamespaceAsync(SqlConnection conn)
    {
        // The namespaces.id column is IDENTITY; the framework namespace row is upserted by name.
        // Race-safe ensure-exists as ONE atomic statement: UPDLOCK + HOLDLOCK takes a key-range lock on
        // ux_namespaces_name that is held through the INSERT. An IF NOT EXISTS ... INSERT would release
        // the check's lock before the INSERT and still race. Mirrors Postgres ON CONFLICT (name) DO NOTHING.
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {IntegrationConfig.TestSchemaName}.namespaces
                (name, owner_team, description, catalog_hash, status_code, created_at_utc, modified_at_utc, version)
            SELECT @name, NULL, NULL, NULL, 10, @now, @now, 0
            WHERE NOT EXISTS (
                SELECT 1 FROM {IntegrationConfig.TestSchemaName}.namespaces WITH (UPDLOCK, HOLDLOCK)
                 WHERE [name] = @name);
            """;
        cmd.Parameters.Add(new SqlParameter("@name", SqlDbType.VarChar, 64) { Value = FrameworkNamespaceName });
        cmd.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = DateTime.UtcNow });
        await cmd.ExecuteNonQueryAsync();
    }

    private static void EnsureTestTarget(string databaseName, string schemaName)
    {
        // Safety rail: refuse to bootstrap unless either the database name or the schema name
        // contains "test". Guards against pointing tests at a production catalog by mistake.
        if (
            databaseName.Contains("test", StringComparison.OrdinalIgnoreCase)
            || schemaName.Contains("test", StringComparison.OrdinalIgnoreCase)
        )
        {
            return;
        }

        throw new InvalidOperationException(
            $"Refusing to bootstrap tests against database '{databaseName}' / schema '{schemaName}': "
                + "either the database name or the schema name must contain 'test'."
        );
    }
}
