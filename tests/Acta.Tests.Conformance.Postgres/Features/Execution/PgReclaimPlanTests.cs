using System.Text.Json;
using Acta.Tests.Conformance.Operations.Execution;
using Acta.Tests.Conformance.Postgres.Testing;
using Npgsql;
using Xunit;

namespace Acta.Tests.Conformance.Postgres.Features.Execution;

/// <summary>
/// The plans reclaim_stuck_jobs runs, captured by auto_explain from inside the routine: both reads of the stuck
/// in-flight rows go through ix_runtimes_worker_inflight, never a scan of runtimes. A partial index serves only a
/// query that states its filter, so the reads restate it; sequential scans are disabled for the capture, which
/// makes the planner take the index whenever the query can use it, however small the shared table is.
/// </summary>
public sealed class PgReclaimPlanTests : ClaimSkewTestBase
{
    [Fact(DisplayName = "reclaim_stuck_jobs reads the stuck in-flight rows through ix_runtimes_worker_inflight")]
    public async Task Reclaim_reads_in_flight_rows_through_the_in_flight_index()
    {
        var ct = TestContext.Current.CancellationToken;
        var (ns, _) = await ClaimantAsync(ct);

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
            "SET LOCAL enable_seqscan = off; SET LOCAL auto_explain.log_min_duration = 0; "
                + "SET LOCAL auto_explain.log_nested_statements = on; SET LOCAL auto_explain.log_format = 'json'; "
                + "SET LOCAL auto_explain.log_level = 'notice'; SET LOCAL client_min_messages = 'notice'",
            ct
        );
        await ExecuteAsync(conn, $"SELECT * FROM {Schema.SchemaName}.reclaim_stuck_jobs({ns})", ct);
        await tx.RollbackAsync(ct);

        var reads = plans
            .Where(p => Text(p, "Query Text").Contains("r.leased_by_worker_id IS NOT NULL", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, reads.Count);
        Assert.All(
            reads,
            read =>
            {
                var nodes = Nodes(read.GetProperty("Plan")).ToList();
                Assert.Contains(nodes, n => Text(n, "Index Name") == "ix_runtimes_worker_inflight");
                Assert.DoesNotContain(nodes, n => Text(n, "Node Type") == "Seq Scan" && Text(n, "Relation Name") == "runtimes");
            }
        );
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
