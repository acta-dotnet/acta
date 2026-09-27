using System.Text.Json;
using Acta.Tests.Conformance.Postgres.Testing;
using Acta.Tests.Conformance.Runtime;
using Npgsql;
using Xunit;

namespace Acta.Tests.Conformance.Postgres.Features.Execution;

/// <summary>
/// The plans claim_batch actually runs, captured by auto_explain from inside the routine: the claim is one
/// ordered scan of ix_runtimes_claim_ready keyed per priority band, and the empty claim's horizon reads the
/// head of each band once. Parked waits and a band of delayed High rows crowd the index around the due rows.
/// </summary>
public sealed class PgClaimPlanTests : ClaimSkewTestBase<PgConformanceFixture>
{
    [Fact(
        DisplayName = "claim_batch seeks ix_runtimes_claim_ready per priority band without sorting, and its horizon reads each band's head"
    )]
    public async Task Claim_seeks_each_band_and_the_horizon_reads_each_band_head()
    {
        var ct = TestContext.Current.CancellationToken;
        var (ns, workerId) = await ClaimantAsync(ct);
        await SeedSkewAsync(ct);

        await using var conn = new NpgsqlConnection(Schema.ConnectionString);
        await conn.OpenAsync(ct);
        var plans = new List<JsonElement>();
        conn.Notice += (_, e) =>
        {
            var json = e.Notice.MessageText.IndexOf('{', StringComparison.Ordinal);
            if (json >= 0)
            {
                plans.Add(JsonDocument.Parse(e.Notice.MessageText[json..]).RootElement.Clone());
            }
        };
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await ExecuteAsync(conn, "LOAD 'auto_explain'", ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            Assert.Skip("Loading auto_explain needs a superuser connection.");
        }
        await ExecuteAsync(
            conn,
            "SET LOCAL auto_explain.log_min_duration = 0; SET LOCAL auto_explain.log_analyze = on; "
                + "SET LOCAL auto_explain.log_nested_statements = on; SET LOCAL auto_explain.log_format = 'json'; "
                + "SET LOCAL auto_explain.log_level = 'notice'; SET LOCAL client_min_messages = 'notice'",
            ct
        );

        // The first claim takes every due row; the second finds none left and reports the horizon.
        Assert.Equal(DueRows, await ClaimAsync(conn, ns, workerId, ct));
        Assert.Equal(0, await ClaimAsync(conn, ns, workerId, ct));
        await tx.RollbackAsync(ct);

        var routine = plans.Where(p => Text(p, "Query Text").Contains("candidates AS", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, routine.Count);

        // The candidate scan reads only the due rows and hands them over in claim order, so nothing sorts.
        var candidates = Nodes(Assert.Single(Nodes(routine[0].GetProperty("Plan")), n => Text(n, "Subplan Name") == "CTE candidates"))
            .ToList();
        Assert.DoesNotContain(candidates, n => Text(n, "Node Type") == "Sort");
        var scan = Assert.Single(candidates, n => Text(n, "Index Name") == "ix_runtimes_claim_ready");
        Assert.Contains("priority_code = ANY", Text(scan, "Index Cond"), StringComparison.Ordinal);
        Assert.Contains("next_run_at_utc <= now()", Text(scan, "Index Cond"), StringComparison.Ordinal);
        Assert.Equal((double)DueRows, scan.GetProperty("Actual Rows").GetDouble());

        var heads = Assert.Single(
            Nodes(routine[1].GetProperty("Plan")),
            n =>
                Text(n, "Index Name") == "ix_runtimes_claim_ready"
                && Text(n, "Index Cond").Contains("band.priority_code", StringComparison.Ordinal)
        );
        Assert.Equal((double)Enum.GetValues<JobPriorityCode>().Length, heads.GetProperty("Actual Loops").GetDouble());
    }

    private async Task<int> ClaimAsync(NpgsqlConnection conn, int ns, int workerId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT id FROM {Schema.SchemaName}.claim_batch(@ns, @worker, 16, 30, false) WHERE id IS NOT NULL";
        cmd.Parameters.AddWithValue("ns", ns);
        cmd.Parameters.AddWithValue("worker", workerId);
        var claimed = 0;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            claimed++;
        }
        return claimed;
    }

    private static async Task ExecuteAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static IEnumerable<JsonElement> Nodes(JsonElement node)
    {
        yield return node;
        if (node.TryGetProperty("Plans", out var children))
        {
            foreach (var child in children.EnumerateArray().SelectMany(Nodes))
            {
                yield return child;
            }
        }
    }

    private static string Text(JsonElement node, string property) =>
        node.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";
}
