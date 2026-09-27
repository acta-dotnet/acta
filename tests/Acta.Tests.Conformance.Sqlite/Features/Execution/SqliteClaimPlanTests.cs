using System.Text.RegularExpressions;
using Acta.Sqlite.Configuration;
using Acta.Tests.Conformance.Runtime;
using Acta.Tests.Conformance.Sqlite.Testing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Acta.Tests.Conformance.Sqlite.Features.Execution;

/// <summary>
/// The query plans of ClaimBatch's claim and horizon statements, read with EXPLAIN QUERY PLAN from the
/// provider's own embedded script: the claim searches ix_runtimes_claim_ready once per priority band in
/// claim order, so SQLite never sorts, and the horizon searches the head of each band. Parked waits, which
/// SQLite sorts first in their band, and a band of delayed High rows crowd the index around the due rows.
/// </summary>
public sealed class SqliteClaimPlanTests : ClaimSkewTestBase<SqliteConformanceFixture>
{
    private const string BandSeek = "USING COVERING INDEX ix_runtimes_claim_ready (namespace_id=? AND priority_code=?";

    [Fact(
        DisplayName = "ClaimBatch searches ix_runtimes_claim_ready per priority band without a sort, and its horizon searches each band's head"
    )]
    public async Task Claim_searches_each_band_and_the_horizon_searches_each_band_head()
    {
        var ct = TestContext.Current.CancellationToken;
        var (ns, workerId) = await ClaimantAsync(ct);
        await SeedSkewAsync(ct);

        await using var conn = new SqliteConnection(SqliteIntegrationSchema.BootstrappedConnectionString);
        await conn.OpenAsync(ct);
        var claim = new List<string>();
        var horizon = new List<string>();
        foreach (var statement in Statements())
        {
            // The claim's candidate statement and the result statement are explained; the rest run only
            // for the temp tables they leave, and the two writes are skipped.
            if (statement.StartsWith("CREATE TEMP TABLE _claimed", StringComparison.Ordinal))
            {
                claim.AddRange(await ExplainAsync(conn, statement, ns, workerId, ct));
            }
            else if (statement.StartsWith("SELECT", StringComparison.Ordinal))
            {
                horizon.AddRange(await ExplainAsync(conn, statement, ns, workerId, ct));
                continue;
            }
            else if (statement.StartsWith("UPDATE", StringComparison.Ordinal) || statement.StartsWith("INSERT", StringComparison.Ordinal))
            {
                continue;
            }

            await using var run = Command(conn, statement, ns, workerId);
            await run.ExecuteNonQueryAsync(ct);
        }

        var claimPlan = "Claim plan:\n" + string.Join("\n", claim);
        Assert.True(
            claim.Any(row => row.Contains(BandSeek + " AND next_run_at_utc>? AND next_run_at_utc<?)", StringComparison.Ordinal)),
            claimPlan
        );
        Assert.False(claim.Any(row => row.Contains("TEMP B-TREE", StringComparison.Ordinal)), claimPlan);
        var horizonPlan = "Horizon plan:\n" + string.Join("\n", horizon);
        Assert.True(horizon.Any(row => row.Contains(BandSeek + " AND next_run_at_utc>?)", StringComparison.Ordinal)), horizonPlan);
        Assert.False(horizon.Any(row => row.StartsWith("SCAN r", StringComparison.Ordinal)), horizonPlan);
    }

    private static IEnumerable<string> Statements()
    {
        using var stream = typeof(SqliteProviderOptions).Assembly.GetManifestResourceStream("Acta.Sqlite.Sql.Execution.ClaimBatch.sql")!;
        using var reader = new StreamReader(stream);
        var sql = reader
            .ReadToEnd()
            .Replace("{{schema}}", "main", StringComparison.Ordinal)
            .Replace("{{now}}", "CAST(unixepoch('now', 'subsec') * 1000 AS INTEGER)", StringComparison.Ordinal);
        // A statement ends at a semicolon that ends its line; a comment may carry one mid-line.
        return Regex.Split(sql, @";[ \t]*\r?\n").Select(StripLeadingComments).Where(s => s.Length > 0);
    }

    private static string StripLeadingComments(string statement)
    {
        var text = statement.TrimStart();
        while (text.StartsWith("/*", StringComparison.Ordinal))
        {
            text = text[(text.IndexOf("*/", StringComparison.Ordinal) + 2)..].TrimStart();
        }
        return text;
    }

    private static async Task<List<string>> ExplainAsync(
        SqliteConnection conn,
        string statement,
        int ns,
        int workerId,
        CancellationToken ct
    )
    {
        await using var cmd = Command(conn, "EXPLAIN QUERY PLAN " + statement, ns, workerId);
        var rows = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(reader.GetString(3));
        }
        return rows;
    }

    private static SqliteCommand Command(SqliteConnection conn, string sql, int ns, int workerId)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@p_namespace_id", ns);
        cmd.Parameters.AddWithValue("@p_leased_by_worker_id", workerId);
        cmd.Parameters.AddWithValue("@p_claim_limit", 16);
        cmd.Parameters.AddWithValue("@p_lease_ttl_seconds", 30);
        cmd.Parameters.AddWithValue("@p_start_executing", 0);
        cmd.Parameters.AddWithValue("@p_excluded_definition_ids", DBNull.Value);
        return cmd;
    }
}
