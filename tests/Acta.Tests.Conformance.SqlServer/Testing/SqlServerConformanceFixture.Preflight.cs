using System.Data.Common;
using Acta.SqlServer;
using Acta.Tests.Conformance.Testing;
using Microsoft.Data.SqlClient;

namespace Acta.Tests.Conformance.SqlServer.Testing;

/// <summary>
/// SQL Server side of the schema bootstrap hooks. A probe is a throwaway schema in the test database
/// carrying at most a <c>migrations</c> table, so it never disturbs <c>acta_test</c>.
/// </summary>
public sealed partial class SqlServerConformanceFixture
{
    public string DialectToken => "mssql";

    public Task RunBootstrapPreflightAsync(string connectionString, string schemaName, CancellationToken ct) =>
        new SqlServerProviderBootstrap(new SqlProviderOptions { ConnectionString = connectionString, Schema = schemaName }).RunAsync(ct);

    public async Task<DbConnection> OpenConnectionAsync(string connectionString, CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    public async Task ApplyMigrationsAsync(string connectionString, string schemaName, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await SqlServerSchemaMigrator.ApplyAsync(connection, schemaName, ct);
    }

    public async ValueTask<IMigrationHistoryProbe> CreateHistoryProbeAsync(IReadOnlyList<string>? history)
    {
        var connectionString = new SqlConnectionStringBuilder(IntegrationConfig.SqlServerConnectionString!)
        {
            TrustServerCertificate = true,
        }.ConnectionString;
        var schema = $"acta_preflight_{Guid.NewGuid():N}"[..30];

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using (var create = conn.CreateCommand())
        {
            // The schema always exists; the ledger inside it does not, which is what separates a
            // never-provisioned database from one holding the wrong history. CREATE SCHEMA must be
            // the only statement in its batch, so it goes through EXEC.
            create.CommandText = $"EXEC (N'CREATE SCHEMA {schema}');";
            await create.ExecuteNonQueryAsync();
        }

        if (history is not null)
        {
            // The provider's own ledger DDL, not a copy of it, so the probe cannot drift from the
            // table the real migration runner creates.
            await using (var ledger = conn.CreateCommand())
            {
                ledger.CommandText = MigrationLedgerDdl.For(DialectToken, schema);
                await ledger.ExecuteNonQueryAsync();
            }

            foreach (var name in history)
            {
                await using var insert = conn.CreateCommand();
                insert.CommandText = $"INSERT INTO {schema}.migrations (name) VALUES (@name);";
                insert.Parameters.AddWithValue("@name", name);
                await insert.ExecuteNonQueryAsync();
            }
        }

        return new SqlServerHistoryProbe(connectionString, schema);
    }

    private sealed class SqlServerHistoryProbe(string connectionString, string schema) : IMigrationHistoryProbe
    {
        public string ConnectionString => connectionString;

        public string SchemaName => schema;

        public async ValueTask DisposeAsync()
        {
            // SQL Server drops a schema only once it is empty, so the ledger goes first.
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();
            await using var drop = conn.CreateCommand();
            drop.CommandText = $"DROP TABLE IF EXISTS {schema}.migrations; DROP SCHEMA IF EXISTS {schema};";
            await drop.ExecuteNonQueryAsync();
        }
    }
}
