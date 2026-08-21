using Acta.Postgres.Configuration;
using Acta.Postgres.Hosting;
using Acta.Tests.Conformance.Testing;
using Npgsql;

namespace Acta.Tests.Conformance.Postgres.Testing;

/// <summary>
/// PostgreSQL side of the migration-history preflight hooks. A probe is a throwaway schema in the
/// test database carrying only a <c>migrations</c> table, so it never disturbs <c>acta_test</c>.
/// </summary>
public sealed partial class PgConformanceFixture
{
    public string DialectToken => "pg";

    public Task RunBootstrapPreflightAsync(string connectionString, string schemaName, CancellationToken ct) =>
        new PostgresProviderBootstrap(new PostgresProviderOptions { ConnectionString = connectionString, Schema = schemaName }).RunAsync(
            ct
        );

    public async ValueTask<IMigrationHistoryProbe> CreateHistoryProbeAsync(IReadOnlyList<(int Version, string Name)>? history)
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
            await using var ledger = conn.CreateCommand();
            ledger.CommandText =
                $"CREATE TABLE {schema}.migrations (version INTEGER NOT NULL, name VARCHAR(256) NOT NULL, "
                + "applied_at_utc TIMESTAMPTZ NOT NULL DEFAULT now(), installed_schema VARCHAR(64) NOT NULL, "
                + "CONSTRAINT pk_migrations PRIMARY KEY (version));";
            await ledger.ExecuteNonQueryAsync();

            foreach (var (version, name) in history)
            {
                await using var insert = conn.CreateCommand();
                insert.CommandText =
                    $"INSERT INTO {schema}.migrations (version, name, installed_schema) VALUES (@version, @name, @schema);";
                insert.Parameters.AddWithValue("@version", version);
                insert.Parameters.AddWithValue("@name", name);
                insert.Parameters.AddWithValue("@schema", schema);
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
