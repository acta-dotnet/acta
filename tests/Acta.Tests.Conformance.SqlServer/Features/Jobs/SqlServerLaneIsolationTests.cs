using System.Data;
using Acta.Tests.Conformance.SqlServer.Testing;
using Acta.Tests.Conformance.Testing;
using Microsoft.Data.SqlClient;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.SqlServer.Features.Jobs;

/// <summary>
/// A laned enqueue decides Ready or Blocked from what it reads after taking the lane lock. Under SNAPSHOT
/// that read comes from the transaction's first snapshot, which can miss a member committed while it
/// waited, so a laned enqueue in a SNAPSHOT caller transaction is refused. Every other level reads
/// committed data per statement or under its locks and is accepted.
/// </summary>
public sealed class SqlServerLaneIsolationTests : ActaRuntimeTestBase<SqlServerConformanceFixture, TestJobsManifest>
{
    // Unpooled: SQL Server keeps a connection's isolation level across pool reuse, and these tests change it.
    private static string ConnectionString =>
        new SqlConnectionStringBuilder(IntegrationConfig.SqlServerConnectionString!)
        {
            TrustServerCertificate = true,
            Pooling = false,
        }.ConnectionString;

    [Fact(DisplayName = "A laned enqueue in a SNAPSHOT caller transaction is refused, single and batched")]
    public async Task Laned_enqueue_under_snapshot_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await AllowSnapshotAsync(conn, ct);

        await using (var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.Snapshot, ct))
        {
            var single = await Assert.ThrowsAsync<EnqueueRejectedException>(async () => await Jobs.EnqueueAsync(tx, Step("orders"), ct));
            Assert.Equal(EnqueueRejectionReason.LaneIsolation, single.Reason);
            await tx.RollbackAsync(ct);
        }

        await using (var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.Snapshot, ct))
        {
            var batched = await Assert.ThrowsAsync<EnqueueRejectedException>(async () =>
                await Jobs.EnqueueBatchAsync(tx, [Step("orders"), Step("billing")], ct)
            );
            Assert.Equal(EnqueueRejectionReason.LaneIsolation, batched.Reason);
            await tx.RollbackAsync(ct);
        }
    }

    [Theory(DisplayName = "A laned enqueue under a locking isolation level lands")]
    [InlineData(IsolationLevel.ReadCommitted)]
    [InlineData(IsolationLevel.RepeatableRead)]
    [InlineData(IsolationLevel.Serializable)]
    public async Task Laned_enqueue_under_locking_isolation_lands(IsolationLevel level)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(level, ct);

        var outcome = await Jobs.EnqueueAsync(tx, Step($"lane-{level}".ToLowerInvariant()), ct);
        await tx.CommitAsync(ct);

        Assert.Equal(JobEnqueueAction.Inserted, outcome.Action);
    }

    // Allowing SNAPSHOT only makes it available to a session that asks for it; it changes no default.
    private static async Task AllowSnapshotAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "IF (SELECT snapshot_isolation_state FROM sys.databases WHERE name = DB_NAME()) = 0 "
            + "ALTER DATABASE CURRENT SET ALLOW_SNAPSHOT_ISOLATION ON;";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private JobEnqueueRequest Step(string lane) =>
        new(TestNamespace, "lane-step", JobPayload.Json(new LaneStep(lane, "isolated")), Lane: lane);
}
