using System.Text.Json;
using Acta.Tests.Conformance.Postgres.Testing;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Acta.Tests.Conformance.Postgres.Features.Execution;

/// <summary>
/// The plan a deploy's read of its namespace's schedules runs, captured by auto_explain: the schedules are reached
/// through the namespace's definitions and each definition's unique name index, never by reading the table whole.
/// <c>schedules</c> has no index on the namespace, so a read that asks it by namespace alone reads every namespace's
/// rows. Sequential scans are disabled for the capture, which makes the planner take an index wherever the query can
/// use one, however small the shared table is.
/// </summary>
public sealed class PgListScheduleStatesPlanTests : ActaStorageTestBase<PgConformanceFixture>
{
    [Fact(DisplayName = "The schedule states read reaches schedules through the namespace's definitions")]
    public async Task The_read_seeks_schedules_through_the_namespaces_definitions()
    {
        var ct = TestContext.Current.CancellationToken;
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
                + "SET LOCAL auto_explain.log_format = 'json'; SET LOCAL auto_explain.log_level = 'notice'; "
                + "SET LOCAL client_min_messages = 'notice'",
            ct
        );

        await Services.GetRequiredService<SchedulesOperations>().ListScheduleStatesAsync(TestNamespaceId, tx, ct);
        await tx.RollbackAsync(ct);

        var read = Assert.Single(plans, p => Text(p, "Query Text").Contains("timing_fingerprint", StringComparison.Ordinal));
        var nodes = Nodes(read.GetProperty("Plan")).ToList();
        // A bitmap scan names its index on a node of its own, under the node that names the table.
        Assert.Contains(nodes, n => Text(n, "Index Name") == "ux_definitions_namespace_name");
        Assert.Contains(nodes, n => Text(n, "Index Name") == "ux_schedules_definition_name");
        Assert.DoesNotContain(nodes, n => Text(n, "Node Type") == "Seq Scan");
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
