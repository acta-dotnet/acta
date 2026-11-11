using System.Globalization;
using Acta.Tests.Conformance.Testing;
using Microsoft.Data.Sqlite;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Sqlite.Features.Schema;

/// <summary>
/// The SQLite operator views show every <c>_utc</c> column as ISO 8601 UTC text with milliseconds and a Z, while the
/// tables keep epoch milliseconds: fixed-width text sorts and compares in time order, so the recipes order and filter
/// on it as they do on a server's timestamp.
/// </summary>
[Deploys("add-numbers")]
public sealed class SqliteViewInstantSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<SqliteViewInstantSpec>>
{
    private const string Iso = "[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9]Z";

    [Fact(DisplayName = "Every _utc column of every SQLite view reads as ISO 8601 UTC text with milliseconds and a Z")]
    public async Task Every_view_instant_is_iso_text()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(AddNumbers(), ct);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        await using var conn = await OpenAsync(ct);
        var views = await ListAsync(conn, "SELECT name FROM sqlite_master WHERE type = 'view' ORDER BY name", ct);
        Assert.Contains("jobs_view", views);
        var offenders = new List<string>();
        var filled = 0;
        foreach (var view in views)
        {
            foreach (var column in await ListAsync(conn, $"SELECT name FROM pragma_table_info('{view}') WHERE name GLOB '*_utc'", ct))
            {
                var invalid = $"SELECT COUNT(*) FROM {view} WHERE typeof({column}) NOT IN ('text', 'null') OR {column} NOT GLOB '{Iso}'";
                if ((await ListAsync(conn, invalid, ct))[0] != "0")
                {
                    offenders.Add($"{view}.{column}");
                }

                filled += (await ListAsync(conn, $"SELECT COUNT({column}) FROM {view}", ct))[0] == "0" ? 0 : 1;
            }
        }

        Assert.Empty(offenders);
        Assert.True(filled > 0, "no view held an instant to check");
    }

    [Fact(DisplayName = "A SQLite view instant is the table's epoch milliseconds to the millisecond, and sorts and compares in time order")]
    public async Task A_view_instant_matches_the_table_and_orders_in_time()
    {
        var ct = TestContext.Current.CancellationToken;
        // Both sides of a second boundary, staged out of order, so a rounding render or a numeric sort shows.
        long[] instants = [1_893_456_000_001, 1_893_455_999_999, 1_893_456_000_000];
        var ids = new List<long>();
        foreach (var instant in instants)
        {
            var enqueued = await Jobs.EnqueueAsync(AddNumbers(), ct);
            await Db.ExecuteRawAsync(
                "UPDATE {schema}.jobs_state SET next_run_at_utc = @p_at WHERE job_id = @p_id",
                ct,
                ("@p_at", instant),
                ("@p_id", enqueued.JobId)
            );
            ids.Add(enqueued.JobId);
        }

        await using var conn = await OpenAsync(ct);
        var mine = $"FROM jobs_view WHERE job_id IN ({string.Join(", ", ids)})";
        Assert.Equal(
            ["2029-12-31T23:59:59.999Z", "2030-01-01T00:00:00.000Z", "2030-01-01T00:00:00.001Z"],
            await ListAsync(conn, $"SELECT next_run_at_utc {mine} ORDER BY next_run_at_utc", ct)
        );
        Assert.Equal(
            [Text(ids[0]), Text(ids[2])],
            await ListAsync(conn, $"SELECT job_id {mine} AND next_run_at_utc > '2029-12-31T23:59:59.999Z' ORDER BY job_id", ct)
        );
        Assert.Equal(
            ids.Select(Text).ToList(),
            await ListAsync(conn, $"SELECT job_id {mine} AND next_run_at_utc > strftime('%Y-%m-%dT%H:%M:%fZ', 'now') ORDER BY job_id", ct)
        );
    }

    private EnqueueJobRequest AddNumbers() => new(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 2)));

    private static string Text(long id) => id.ToString(CultureInfo.InvariantCulture);

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(Schema.ConnectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    private static async Task<List<string>> ListAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var command = conn.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(ct);
        var values = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            values.Add(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!);
        }

        return values;
    }
}
