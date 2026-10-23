using System.Text.RegularExpressions;
using Acta.Tests.Conformance.Operations.Execution;
using Acta.Tests.Conformance.Sqlite.Testing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Acta.Tests.Conformance.Sqlite.Features.Execution;

/// <summary>
/// The query plan of ReclaimStuckJobs' read of the stuck in-flight rows, from the provider's own embedded script:
/// it searches ix_jobs_state_worker_inflight rather than scanning jobs_state. SQLite uses a partial index only for a
/// query that states the index's filter, so the read restates it.
/// </summary>
public sealed class SqliteReclaimPlanTests : ClaimSkewTestBase
{
    [Fact(DisplayName = "ReclaimStuckJobs reads the stuck in-flight rows through ix_jobs_state_worker_inflight")]
    public async Task Reclaim_reads_in_flight_rows_through_the_in_flight_index()
    {
        var ct = TestContext.Current.CancellationToken;
        var (ns, _) = await ClaimantAsync(ct);

        await using var conn = new SqliteConnection(SqliteIntegrationSchema.BootstrappedConnectionString);
        await conn.OpenAsync(ct);
        var plan = new List<string>();
        foreach (var statement in Statements())
        {
            // The read of the stuck rows is explained; the temp-table setup before it runs, and every write and
            // result statement is skipped.
            if (statement.StartsWith("CREATE TEMP TABLE _reclaim_jobs", StringComparison.Ordinal))
            {
                plan.AddRange(await ExplainAsync(conn, statement, ns, ct));
                break;
            }
            if (
                statement.StartsWith("CREATE TEMP TABLE", StringComparison.Ordinal)
                || statement.StartsWith("DROP", StringComparison.Ordinal)
            )
            {
                await using var run = Command(conn, statement, ns);
                await run.ExecuteNonQueryAsync(ct);
            }
        }

        var text = "Reclaim plan:\n" + string.Join("\n", plan);
        Assert.True(plan.Any(row => row.Contains("INDEX ix_jobs_state_worker_inflight", StringComparison.Ordinal)), text);
        Assert.False(plan.Any(row => row.StartsWith("SCAN r", StringComparison.Ordinal)), text);
    }

    private static IEnumerable<string> Statements()
    {
        var sql = new SqlResourceCatalog("sqlite", "main").Load("ReclaimStuckJobs");

        // A statement ends at a semicolon that ends its line; a comment may carry one mid-line.
        return Regex.Split(sql, @";[ \t]*\r?\n").Select(StripLeadingComments).Where(s => s.Length > 0);
    }

    private static string StripLeadingComments(string statement)
    {
        var text = statement.TrimStart();
        while (text.StartsWith("/*", StringComparison.Ordinal) || text.StartsWith("--", StringComparison.Ordinal))
        {
            text = text.StartsWith("--", StringComparison.Ordinal)
                ? text[(text.IndexOf('\n') + 1)..].TrimStart()
                : text[(text.IndexOf("*/", StringComparison.Ordinal) + 2)..].TrimStart();
        }
        return text;
    }

    private static async Task<List<string>> ExplainAsync(SqliteConnection conn, string statement, int ns, CancellationToken ct)
    {
        await using var cmd = Command(conn, "EXPLAIN QUERY PLAN " + statement, ns);
        var rows = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(reader.GetString(3));
        }
        return rows;
    }

    private static SqliteCommand Command(SqliteConnection conn, string sql, int ns)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@p_namespace_id", ns);
        return cmd;
    }
}
