using System.Globalization;
using Acta.Payloads;
using Acta.Tests.Conformance.Postgres.Testing;
using Acta.Tests.Conformance.Testing;
using Npgsql;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Postgres.Features.Jobs;

/// <summary>
/// PostgreSQL aborted-transaction behavior for direct transactional enqueue. The enqueue routine runs inside
/// the caller transaction, so a database-level rejection aborts the whole transaction: every subsequent
/// command fails with 25P02 until the caller rolls back, and only the caller's rollback discards the business
/// write. Provider-specific, so it lives in the PostgreSQL head.
/// </summary>
public sealed class PostgresAbortedTransactionTests : ActaRuntimeTestBase<PgConformanceFixture, TestJobsManifest>
{
    private const string ProbeTable = "acta_txn_aborted_probe";

    [Fact(DisplayName = "A rejected enqueue aborts the transaction so later commands fail with 25P02 until the caller rolls back")]
    public async Task Rejection_aborts_the_transaction_until_rollback()
    {
        var ct = TestContext.Current.CancellationToken;
        var marker = TestKey("pg-aborted");

        await using var conn = new NpgsqlConnection(IntegrationConfig.PostgresConnectionString!);
        await conn.OpenAsync(ct);
        var probe = await Fixture.EnsureBusinessProbeTableAsync(conn, Schema.SchemaName, ProbeTable);

        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            await InsertBusinessRowAsync(conn, tx, probe, marker, ct);

            // An unknown job is a database-level rejection inside the caller transaction.
            await Assert.ThrowsAnyAsync<Exception>(async () =>
                await Jobs.EnqueueAsync(tx, new JobEnqueueRequest(TestNamespace, "no-such-job", JobPayload.None), ct)
            );

            // PostgreSQL has aborted the transaction, so any further command is refused with 25P02.
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT 1;";
            var aborted = await Assert.ThrowsAsync<PostgresException>(async () => await cmd.ExecuteScalarAsync(ct));
            Assert.Equal("25P02", aborted.SqlState);

            await tx.RollbackAsync(ct);
        }

        // Only the caller's rollback discarded the business write.
        Assert.Equal(0, await CountBusinessRowsAsync(conn, probe, marker, ct));
    }

    private static async Task InsertBusinessRowAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string probe,
        string marker,
        CancellationToken ct
    )
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"INSERT INTO {probe} (marker) VALUES (@marker)";
        cmd.Parameters.AddWithValue("@marker", marker);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> CountBusinessRowsAsync(NpgsqlConnection conn, string probe, string marker, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {probe} WHERE marker = @marker";
        cmd.Parameters.AddWithValue("@marker", marker);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }
}
