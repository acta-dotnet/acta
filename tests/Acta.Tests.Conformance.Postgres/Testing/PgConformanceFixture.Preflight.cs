using System.Data.Common;
using Acta.Postgres;
using Acta.Tests.Conformance.Testing;
using Npgsql;

namespace Acta.Tests.Conformance.Postgres.Testing;

/// <summary>
/// PostgreSQL side of the schema bootstrap hooks. A probe is a throwaway schema in the test database
/// carrying at most a <c>migrations</c> table, so it never disturbs <c>acta_test</c>.
/// </summary>
public sealed partial class PgConformanceFixture
{
    public string DialectToken => "pg";

    public Task RunBootstrapPreflightAsync(string connectionString, string schemaName, CancellationToken ct) =>
        new PostgresProviderBootstrap(new PostgresProviderOptions { ConnectionString = connectionString, Schema = schemaName }).RunAsync(
            ct
        );

    public async Task<DbConnection> OpenConnectionAsync(string connectionString, CancellationToken ct)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    public async Task ApplyMigrationsAsync(string connectionString, string schemaName, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await PostgresSchemaMigrator.ApplyAsync(connection, schemaName, ct);
    }

    public async ValueTask<IMigrationHistoryProbe> CreateHistoryProbeAsync(IReadOnlyList<string>? history)
    {
        var connectionString = IntegrationConfig.PostgresConnectionString!;
        var schema = $"acta_preflight_{Guid.NewGuid():N}"[..30];

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using (var create = conn.CreateCommand())
        {
            // The schema always exists; the ledger inside it does not, which is what separates a
            // never-provisioned database from one holding the wrong history.
            create.CommandText = $"CREATE SCHEMA {schema};";
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

        return new PgHistoryProbe(connectionString, schema);
    }

    private sealed class PgHistoryProbe(string connectionString, string schema) : IMigrationHistoryProbe
    {
        public string ConnectionString => connectionString;

        public string SchemaName => schema;

        public async ValueTask DisposeAsync()
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            await using var drop = conn.CreateCommand();
            drop.CommandText = $"DROP SCHEMA IF EXISTS {schema} CASCADE;";
            await drop.ExecuteNonQueryAsync();
        }
    }
}
