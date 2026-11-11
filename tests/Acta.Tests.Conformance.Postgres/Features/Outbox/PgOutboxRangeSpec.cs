using Acta.Postgres;
using Acta.Tests.Conformance.Postgres.Testing;
using Acta.Tests.Conformance.Testing;
using Npgsql;
using Xunit;

namespace Acta.Tests.Conformance.Postgres.Features.Outbox;

/// <summary>
/// The PostgreSQL outbox table refuses a value the claim projection cannot read: smallint holds a priority of 256, which
/// is no byte, and timestamptz holds a run instant past year 9999 or before year 1, which is no DateTime, so both ranges
/// are checks.
/// </summary>
public sealed class PgOutboxRangeSpec
{
    [Fact(DisplayName = "The pg outbox table keeps priority 255 and refuses 256")]
    public async Task A_priority_past_255_is_refused()
    {
        await WithTableAsync(
            async (conn, schema, table, ct) =>
            {
                await ExecAsync(conn, Insert(schema, table, "job_priority_code", "255"), ct);

                var refused = await Assert.ThrowsAsync<PostgresException>(() =>
                    ExecAsync(conn, Insert(schema, table, "job_priority_code", "256"), ct)
                );
                Assert.Equal($"ck_{table}_priority_range", refused.ConstraintName);
            }
        );
    }

    [Theory(DisplayName = "The pg outbox table refuses a run instant no DateTime holds")]
    [InlineData("10000-01-01 00:00:00+00")]
    [InlineData("0001-12-31 23:59:59+00 BC")]
    [InlineData("infinity")]
    [InlineData("-infinity")]
    public async Task A_run_instant_past_the_datetime_range_is_refused(string instant)
    {
        await WithTableAsync(
            async (conn, schema, table, ct) =>
            {
                await ExecAsync(conn, Insert(schema, table, "next_run_at_utc", "'0001-01-01 00:00:00+00'"), ct);
                await ExecAsync(conn, Insert(schema, table, "next_run_at_utc", "'9999-12-31 23:59:59.999999+00'"), ct);

                var refused = await Assert.ThrowsAsync<PostgresException>(() =>
                    ExecAsync(conn, Insert(schema, table, "next_run_at_utc", $"'{instant}'"), ct)
                );
                Assert.Equal($"ck_{table}_next_run_instant", refused.ConstraintName);
            }
        );
    }

    private static async Task WithTableAsync(Func<NpgsqlConnection, string, string, CancellationToken, Task> body)
    {
        var connString = IntegrationConfig.PostgresConnectionString;
        if (connString is null)
        {
            Assert.Skip("ACTA_TEST_PG is not set.");
        }

        var ct = TestContext.Current.CancellationToken;
        await ActaSharedDatabase.EnsureReadyAsync(new PgConformanceFixture());
        var schema = IntegrationConfig.TestSchemaName;
        var table = $"acta_outbox_rg_{Guid.NewGuid():N}"[..30];
        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync(ct);
        try
        {
            await ExecAsync(conn, PostgresMigrations.CreateOutboxScript(table, schema), ct);
            await body(conn, schema, table, ct);
        }
        finally
        {
            await ExecAsync(conn, $"DROP TABLE IF EXISTS {schema}.{table};", ct);
        }
    }

    private static string Insert(string schema, string table, string column, string literal) =>
        $"INSERT INTO {schema}.{table} (outbox_id, namespace_name, job_name, input_format_id, deduplication_key, {column}) "
        + $"VALUES ('{Guid.NewGuid()}', 'orders', 'send', 0, '{Guid.NewGuid():N}', {literal});";

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
