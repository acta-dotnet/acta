using System.Text.RegularExpressions;
using Acta.Tests.Conformance.Operations.Execution;
using Acta.Tests.Conformance.Sqlite.Testing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Acta.Tests.Conformance.Sqlite.Features.Execution;

/// <summary>
/// The query plans of ReclaimStuckJobs, from the provider's own embedded script. The read of the stuck in-flight rows
/// searches ix_jobs_state_worker_inflight rather than scanning jobs_state: SQLite uses a partial index only for a
/// query that states the index's filter, so the read restates it. The lane repair's two windows search
/// ix_lanes_namespace, so the walk reads its own namespace's lanes and no other's.
/// </summary>
public sealed class SqliteReclaimPlanTests : ClaimSkewTestBase
{
    [Fact(DisplayName = "ReclaimStuckJobs reads the stuck in-flight rows through ix_jobs_state_worker_inflight")]
    public async Task Reclaim_reads_in_flight_rows_through_the_in_flight_index()
    {
        var ct = TestContext.Current.CancellationToken;
        var (ns, _) = await ClaimantAsync(ct);

        var plan = (await PlansAsync(ns, ct, "CREATE TEMP TABLE _reclaim_jobs")).Single();

        var text = "Reclaim plan:\n" + string.Join("\n", plan);
        Assert.True(plan.Any(row => row.Contains("INDEX ix_jobs_state_worker_inflight", StringComparison.Ordinal)), text);
        Assert.False(plan.Any(row => row.StartsWith("SCAN r", StringComparison.Ordinal)), text);
    }

    [Fact(DisplayName = "ReclaimStuckJobs walks a namespace's lanes through ix_lanes_namespace, in id order with no sort")]
    public async Task Reclaim_walks_a_namespaces_lanes_through_their_index()
    {
        var ct = TestContext.Current.CancellationToken;
        var (ns, _) = await ClaimantAsync(ct);

        var windows = await PlansAsync(ns, ct, "CREATE TEMP TABLE _repair_window", "INSERT INTO temp._repair_window");

        Assert.Equal(2, windows.Count);
        Assert.All(windows, plan =>
        {
            // The index is named: a search of the key from the start id is a search too, and reads every namespace's lanes.
            var text = "Lane window plan:\n" + string.Join("\n", plan);
            Assert.True(
                plan.Any(row =>
                    row.StartsWith("SEARCH l USING", StringComparison.Ordinal)
                    && row.Contains("INDEX ix_lanes_namespace (namespace_id=? AND lane_id", StringComparison.Ordinal)
                ),
                text
            );
            Assert.False(plan.Any(row => row.Contains("TEMP B-TREE", StringComparison.Ordinal)), text);
        });
    }

    /// <summary>
    /// The plan of each statement that starts with one of <paramref name="explained"/>, in script order, read up to
    /// the last of them. The temp-table setup on the way runs, an explained one included unless it is the last, so a
    /// later statement finds its tables; every write and result statement is skipped.
    /// </summary>
    private static async Task<List<List<string>>> PlansAsync(int ns, CancellationToken ct, params string[] explained)
    {
        await using var conn = new SqliteConnection(SqliteIntegrationSchema.BootstrappedConnectionString);
        await conn.OpenAsync(ct);
        var plans = new List<List<string>>();
        foreach (var statement in Statements())
        {
            if (explained.Any(prefix => statement.StartsWith(prefix, StringComparison.Ordinal)))
            {
                plans.Add(await ExplainAsync(conn, statement, ns, ct));
                if (plans.Count == explained.Length)
                {
                    break;
                }
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

        return plans;
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
