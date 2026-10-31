using Acta.Postgres;
using Acta.Tests.Conformance.Postgres.Testing;
using Acta.Tests.Conformance.Testing;
using Npgsql;
using Xunit;

namespace Acta.Tests.Conformance.Postgres.Features.Outbox;

/// <summary>
/// The PostgreSQL outbox table refuses a priority the claim projection cannot read into a byte: smallint holds 256,
/// so the range is a check.
/// </summary>
public sealed class PgOutboxPriorityRangeSpec
{
    [Fact(DisplayName = "The pg outbox table keeps priority 255 and refuses 256")]
    public async Task A_priority_past_255_is_refused()
    {
        var connString = IntegrationConfig.PostgresConnectionString;
        if (connString is null)
        {
            Assert.Skip("ACTA_TEST_PG is not set.");
        }

        var ct = TestContext.Current.CancellationToken;
        await ActaSharedDatabase.EnsureReadyAsync(new PgConformanceFixture());
        var schema = IntegrationConfig.TestSchemaName;
        var table = $"acta_outbox_pr_{Guid.NewGuid():N}"[..30];
        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync(ct);
        try
        {
            await ExecAsync(conn, PostgresOutboxDdl.CreateScript(table, schema), ct);
            await ExecAsync(conn, Insert(schema, table, 255), ct);

            var refused = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(conn, Insert(schema, table, 256), ct));
            Assert.Equal($"ck_{table}_priority_range", refused.ConstraintName);
        }
        finally
        {
            await ExecAsync(conn, $"DROP TABLE IF EXISTS {schema}.{table};", ct);
        }
    }

    private static string Insert(string schema, string table, int priority) =>
        $"INSERT INTO {schema}.{table} (outbox_id, job_namespace, job_name, input_format_id, deduplication_key, priority_code) "
        + $"VALUES ('{Guid.NewGuid()}', 'orders', 'send', 0, '{Guid.NewGuid():N}', {priority});";

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
