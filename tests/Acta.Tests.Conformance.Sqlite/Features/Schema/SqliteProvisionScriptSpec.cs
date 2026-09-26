using System.Text.RegularExpressions;
using Acta.Tests.Conformance.Testing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Acta.Tests.Conformance.Sqlite.Features.Schema;

/// <summary>
/// The published provisioning script (docs/reference/schema-sqlite.sql) must provision a working
/// database when run verbatim, mirroring the pg/mssql provision-script specs so all three published
/// files are execution-proven. SQLite targets <c>main</c>, so a fresh in-memory database stands in
/// for the fresh schema.
/// </summary>
public sealed partial class SqliteProvisionScriptSpec
{
    [Fact(DisplayName = "The published sqlite provision script provisions a fresh database verbatim")]
    public async Task Published_script_provisions_a_fresh_database()
    {
        var ct = TestContext.Current.CancellationToken;
        var script = File.ReadAllText(Path.Combine(IntegrationConfig.FindRepoRoot(), "docs", "reference", "schema-sqlite.sql"));

        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync(ct);
        // Twice on purpose: the header promises that a re-run on a database the script provisioned
        // applies no migration twice, and the migration-row count below is what proves it.
        for (var pass = 0; pass < 2; pass++)
        {
            await using var provision = conn.CreateCommand();
            provision.CommandText = script;
            await provision.ExecuteNonQueryAsync(ct);
        }

        // One history row per migration section in the file plus the version-0 baseline-stamp row,
        // no more (the double run must not stamp anything twice), counted from the script's own
        // BEGIN banners, plus the version-0 baseline row and the object package row the script records.
        var migrations = BeginBanner().Matches(script).Count + 2;
        Assert.True(migrations > 1, "the published script contains no migration banners");

        await using var probe = conn.CreateCommand();
        probe.CommandText = "SELECT (SELECT COUNT(*) FROM main.migrations), (SELECT COUNT(*) FROM main.jobs_view)";
        await using var reader = await probe.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        Assert.Equal(migrations, reader.GetInt64(0));
        Assert.Equal(0, reader.GetInt64(1));
    }

    [Fact(DisplayName = "A sqlite database provisioned by the script runs with migrations disabled and keeps its data across a re-run")]
    public async Task Script_provisioned_database_runs_and_keeps_its_data_across_a_rerun()
    {
        var ct = TestContext.Current.CancellationToken;
        var script = File.ReadAllText(Path.Combine(IntegrationConfig.FindRepoRoot(), "docs", "reference", "schema-sqlite.sql"));
        // A file rather than :memory:, because the script, the host, and the re-run each open their own
        // connection and must all meet the same database.
        var path = Path.Combine(Path.GetTempPath(), $"acta-provision-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ConnectionString;
        void ApplyProvider(IActaBuilder builder, string schema) =>
            builder.UseSqlite(o =>
            {
                o.ConnectionString = connectionString;
                o.Schema = schema;
            });

        try
        {
            await RunScriptAsync(connectionString, script, ct);
            var job = await ScriptProvisionedRuntime.RunJobAsync(ApplyProvider, "main", 2, 3, ct);
            await RunScriptAsync(connectionString, script, ct);
            await ScriptProvisionedRuntime.AssertSucceededAsync(ApplyProvider, "main", job, 5, ct);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException) { }
            }
        }
    }

    private static async Task RunScriptAsync(string connectionString, string script, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var provision = conn.CreateCommand();
        provision.CommandText = script;
        await provision.ExecuteNonQueryAsync(ct);
    }

    // The per-migration section banner the emitter writes above every migration.
    [GeneratedRegex(@"^-- ===== BEGIN M[0-9]{3}_", RegexOptions.Multiline)]
    private static partial Regex BeginBanner();
}
