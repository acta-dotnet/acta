using Acta.Tests.Conformance.Testing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Acta.Tests.Conformance.Sqlite.Features.Schema;

/// <summary>
/// The published provisioning script (docs/reference/provision/sqlite.sql) must provision a working
/// database when run verbatim, mirroring the pg/mssql provision-script specs so all three published
/// files are execution-proven. SQLite targets <c>main</c>, so a fresh in-memory database stands in
/// for the fresh schema.
/// </summary>
public sealed class SqliteProvisionScriptSpec
{
    [Fact(DisplayName = "The published sqlite provision script provisions a fresh database verbatim")]
    public async Task Published_script_provisions_a_fresh_database()
    {
        var ct = TestContext.Current.CancellationToken;
        var script = File.ReadAllText(Path.Combine(IntegrationConfig.FindRepoRoot(), "docs", "reference", "provision", "sqlite.sql"));

        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync(ct);
        await using (var provision = conn.CreateCommand())
        {
            provision.CommandText = script;
            await provision.ExecuteNonQueryAsync(ct);
        }

        await using var probe = conn.CreateCommand();
        probe.CommandText = "SELECT (SELECT COUNT(*) FROM main.migrations WHERE version = 1), (SELECT COUNT(*) FROM main.jobs_view)";
        await using var reader = await probe.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        Assert.Equal(1, reader.GetInt64(0));
        Assert.Equal(0, reader.GetInt64(1));
    }
}
