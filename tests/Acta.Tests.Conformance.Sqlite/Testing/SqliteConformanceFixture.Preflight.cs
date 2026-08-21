using Acta.Sqlite.Configuration;
using Acta.Sqlite.Hosting;
using Acta.Tests.Conformance.Testing;
using Microsoft.Data.Sqlite;

namespace Acta.Tests.Conformance.Sqlite.Testing;

/// <summary>
/// SQLite side of the migration-history preflight hooks. SQLite has no schema container, so a probe
/// is its own temp database file rather than a schema inside the shared one.
/// </summary>
public sealed partial class SqliteConformanceFixture
{
    public string DialectToken => "sqlite";

    public Task RunBootstrapPreflightAsync(string connectionString, string schemaName, CancellationToken ct) =>
        new SqliteProviderBootstrap(new SqliteProviderOptions { ConnectionString = connectionString, Schema = schemaName }).RunAsync(ct);

    public async ValueTask<IMigrationHistoryProbe> CreateHistoryProbeAsync(IReadOnlyList<(int Version, string Name)>? history)
    {
        var path = Path.Combine(Path.GetTempPath(), $"acta-preflight-{Guid.NewGuid():N}.db");
        // Pooling off so the file is closed the moment each connection is, and the probe can delete
        // itself on dispose without racing a pooled handle.
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString;

        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync();
        if (history is not null)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE main.migrations (version INTEGER NOT NULL, name TEXT NOT NULL, "
                + "applied_at_utc TEXT NOT NULL, installed_schema TEXT NOT NULL, "
                + "CONSTRAINT pk_migrations PRIMARY KEY (version)) STRICT;";
            await cmd.ExecuteNonQueryAsync();

            foreach (var (version, name) in history)
            {
                await using var insert = conn.CreateCommand();
                insert.CommandText =
                    "INSERT INTO main.migrations (version, name, applied_at_utc, installed_schema) "
                    + "VALUES (@version, @name, '2026-01-01T00:00:00Z', 'main');";
                insert.Parameters.AddWithValue("@version", version);
                insert.Parameters.AddWithValue("@name", name);
                await insert.ExecuteNonQueryAsync();
            }
        }

        return new SqliteHistoryProbe(path, connectionString);
    }

    private sealed class SqliteHistoryProbe(string path, string connectionString) : IMigrationHistoryProbe
    {
        public string ConnectionString => connectionString;

        public string SchemaName => "main";

        public ValueTask DisposeAsync()
        {
            File.Delete(path);
            return ValueTask.CompletedTask;
        }
    }
}
