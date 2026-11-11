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
/// committed data per statement or under its locks and is accepted. A lane an open enqueue created is skipped by the
/// job list's lane filter, which tells only once lanes is large enough for the engine to lock pages for a scan.
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
                await Jobs.EnqueueJobsAsync(tx, [Step("orders"), Step("billing")], ct)
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

        Assert.Equal(EnqueueJobAction.Inserted, outcome.Action);
    }

    [Fact(DisplayName = "An enqueue under repeatable read reads its own namespace and leaves another's row free")]
    public async Task Enqueue_under_repeatable_read_seeks_its_own_namespace()
    {
        var ct = TestContext.Current.CancellationToken;
        var other = await new ActaTestSeeder(Db).SeedJobNamespaceAsync(Unique("other"), ownerTeam: "test", ct: ct);
        await using var holder = new SqlConnection(ConnectionString);
        await holder.OpenAsync(ct);
        await using var hold = (SqlTransaction)await holder.BeginTransactionAsync(ct);
        // A deploy holds its namespace's row from its version stamp until it commits.
        await using (
            var command = new SqlCommand($"UPDATE {Db.Schema}.namespaces SET version = version WHERE namespace_id = @id", holder, hold)
        )
        {
            command.Parameters.AddWithValue("@id", other);
            Assert.Equal(1, await command.ExecuteNonQueryAsync(ct));
        }

        var enqueue = EnqueueUnderRepeatableReadAsync(ct);
        // The held row stays locked until the fact ends, so an enqueue that waited on it would never return.
        var first = await Task.WhenAny(enqueue, Task.Delay(SpecWaits.Gate, ct));
        Assert.True(first == enqueue, "the enqueue waited on another namespace's row");
        Assert.Equal(EnqueueJobAction.Inserted, await enqueue);
    }

    /// <summary>Repeatable read keeps every row a read touches share-locked until the caller commits.</summary>
    private async Task<EnqueueJobAction> EnqueueUnderRepeatableReadAsync(CancellationToken ct)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var outcome = await Jobs.EnqueueAsync(tx, Step("lane-other"), ct);
        await tx.CommitAsync(ct);
        return outcome.Action;
    }

    // Allowing SNAPSHOT only makes it available to a session that asks for it; it changes no default.
    [Fact(DisplayName = "The job list filtered by a lane answers past a lane an open enqueue created")]
    public async Task Job_list_by_lane_skips_an_uncommitted_lane()
    {
        var ct = TestContext.Current.CancellationToken;
        var lane = Unique("open");
        await using var connection = await Db.OpenConnectionAsync(ct);
        await using var application = await connection.BeginTransactionAsync(ct);
        await Jobs.EnqueueAsync(application, Step(lane), ct);

        // Across every namespace: with one named, the list reaches lanes by key and never scans the table.
        var list = Operations.Ledger.ListJobsAsync(new ListJobsQuery(IncludeTotal: true, Lane: lane), ct).AsTask();
        var first = await Task.WhenAny(list, Task.Delay(SpecWaits.Gate, ct));
        Assert.True(first == list, "the job list waited on an uncommitted lane");
        Assert.Empty((await list).Items);
    }

    private static Task AllowSnapshotAsync(SqlConnection conn, CancellationToken ct) =>
        TestDdl.RunAsync(async () =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "IF (SELECT snapshot_isolation_state FROM sys.databases WHERE name = DB_NAME()) = 0 "
                + "ALTER DATABASE CURRENT SET ALLOW_SNAPSHOT_ISOLATION ON;";
            await cmd.ExecuteNonQueryAsync(ct);
        });

    private EnqueueJobRequest Step(string lane) =>
        new(TestNamespace, "lane-step", JobPayload.Json(new LaneStep(lane, "isolated")), Lane: lane);
}
