using System.Globalization;
using Acta.Payloads;
using Acta.Tests.Conformance.SqlServer.Testing;
using Acta.Tests.Conformance.Testing;
using Microsoft.Data.SqlClient;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.SqlServer.Features.Jobs;

/// <summary>
/// SQL Server transaction-count ownership: the <c>enqueue_one</c>/<c>enqueue_batch</c> procedures capture
/// their entry <c>@@TRANCOUNT</c> and start, commit, or roll back a transaction only when they entered with
/// none. Inside a caller transaction they neither commit nor roll back it - the caller owns completion. On a
/// database rejection <c>XACT_ABORT</c> terminates the caller transaction, so the caller cannot commit the
/// business work either. Provider-specific, so it lives in the SQL Server head.
/// </summary>
public sealed class SqlServerTransactionOwnershipTests : ActaRuntimeTestBase<SqlServerConformanceFixture, TestJobsManifest>
{
    private const string ProbeTable = "acta_txn_own_probe";

    [Fact(DisplayName = "The enqueue procedure joins the caller transaction and does not commit it")]
    public async Task Procedure_does_not_commit_the_caller_transaction()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var conn = new SqlConnection(ConnectionString());
        await conn.OpenAsync(ct);

        JobEnqueueOutcome outcome;
        await using (var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct))
        {
            outcome = await Jobs.EnqueueAsync(tx, Request(), ct);
            Assert.Equal(JobEnqueueAction.Inserted, outcome.Action);

            // The procedure entered with an open transaction, so it ran the inserts but skipped its own
            // COMMIT: the caller's transaction count is still 1 and completion remains the caller's.
            Assert.Equal(1, await TrancountAsync(conn, tx, ct));

            await tx.RollbackAsync(ct);
        }

        // Because the procedure did not commit, the caller's rollback discarded the provisional job.
        Assert.Null(await Jobs.GetStatusAsync(outcome, ct));
    }

    [Fact(DisplayName = "A database rejection under XACT_ABORT terminates the caller transaction and persists nothing")]
    public async Task Rejection_terminates_the_caller_transaction()
    {
        var ct = TestContext.Current.CancellationToken;
        var marker = TestKey("mssql-own-reject");

        await using var conn = new SqlConnection(ConnectionString());
        await conn.OpenAsync(ct);
        var probe = await Fixture.EnsureBusinessProbeTableAsync(conn, Schema.SchemaName, ProbeTable);

        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        await InsertBusinessRowAsync(conn, tx, probe, marker, ct);

        // An unknown job is a database-level rejection: the procedure THROWs and, under XACT_ABORT, SQL
        // Server terminates the whole caller transaction rather than letting Acta complete it.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await Jobs.EnqueueAsync(tx, new JobEnqueueRequest(TestNamespace, "no-such-job", JobPayload.None), ct)
        );

        // The transaction is gone (count 0), so the business write it carried never commits.
        Assert.Equal(0, await TrancountAsync(conn, null, ct));
        Assert.Equal(0, await CountBusinessRowsAsync(conn, probe, marker, ct));
    }

    private static string ConnectionString() =>
        new SqlConnectionStringBuilder(IntegrationConfig.SqlServerConnectionString!) { TrustServerCertificate = true }.ConnectionString;

    private JobEnqueueRequest Request() =>
        new(TestNamespace, "add-numbers", JobPayload.CopyBytes(JobPayloadFormat.Json, "{\"left\":1,\"right\":1}"u8.ToArray()));

    private static async Task<int> TrancountAsync(SqlConnection conn, SqlTransaction? tx, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT @@TRANCOUNT;";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task InsertBusinessRowAsync(
        SqlConnection conn,
        SqlTransaction tx,
        string probe,
        string marker,
        CancellationToken ct
    )
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"INSERT INTO {probe} (marker) VALUES (@marker)";
        cmd.Parameters.Add(new SqlParameter("@marker", marker));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> CountBusinessRowsAsync(SqlConnection conn, string probe, string marker, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {probe} WHERE marker = @marker";
        cmd.Parameters.Add(new SqlParameter("@marker", marker));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }
}
