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
            // The provider's own ledger DDL, not a copy of it, so the probe cannot drift from the
            // table the real migration runner creates.
            await using (var ledger = conn.CreateCommand())
            {
                ledger.CommandText = MigrationLedgerDdl.For(DialectToken, "main");
                await ledger.ExecuteNonQueryAsync();
            }

            foreach (var (version, name) in history)
            {
                await using var insert = conn.CreateCommand();
                insert.CommandText = "INSERT INTO main.migrations (version, name, installed_schema) VALUES (@version, @name, 'main');";
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
            // Best-effort, like every other fixture teardown here: cleanup failing must not turn a
            // spec that already proved its point into a red run. Pooling=false closes the handle with
            // the connection, but a Windows scanner or indexer can still hold the file for a moment,
            // and the probe lives under the temp directory either way.
            try
            {
                File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            return ValueTask.CompletedTask;
        }
    }
}
