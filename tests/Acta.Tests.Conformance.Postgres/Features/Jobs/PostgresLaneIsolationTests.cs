using System.Data;
using Acta.Tests.Conformance.Postgres.Testing;
using Acta.Tests.Conformance.Testing;
using Npgsql;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Postgres.Features.Jobs;

/// <summary>
/// A laned enqueue decides Ready or Blocked from what it reads after taking the lane lock. Under
/// REPEATABLE READ or SERIALIZABLE that read comes from the transaction's first snapshot, which can miss
/// a member committed while it waited, so a laned enqueue in such a caller transaction is refused.
/// </summary>
public sealed class PostgresLaneIsolationTests : ActaRuntimeTestBase<PgConformanceFixture, TestJobsManifest>
{
    [Theory(DisplayName = "A laned enqueue in a snapshot-isolated caller transaction is refused, single and batched")]
    [InlineData(IsolationLevel.RepeatableRead)]
    [InlineData(IsolationLevel.Serializable)]
    public async Task Laned_enqueue_under_snapshot_isolation_is_refused(IsolationLevel level)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var conn = new NpgsqlConnection(IntegrationConfig.PostgresConnectionString!);
        await conn.OpenAsync(ct);

        await using (var tx = await conn.BeginTransactionAsync(level, ct))
        {
            var single = await Assert.ThrowsAsync<EnqueueRejectedException>(async () => await Jobs.EnqueueAsync(tx, Step("orders"), ct));
            Assert.Equal(EnqueueRejectionReason.LaneIsolation, single.Reason);
            await tx.RollbackAsync(ct);
        }

        await using (var tx = await conn.BeginTransactionAsync(level, ct))
        {
            var batched = await Assert.ThrowsAsync<EnqueueRejectedException>(async () =>
                await Jobs.EnqueueBatchAsync(tx, [Step("orders"), Step("billing")], ct)
            );
            Assert.Equal(EnqueueRejectionReason.LaneIsolation, batched.Reason);
            await tx.RollbackAsync(ct);
        }
    }

    [Fact(DisplayName = "An unlaned enqueue in a snapshot-isolated caller transaction still lands")]
    public async Task Unlaned_enqueue_under_snapshot_isolation_lands()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var conn = new NpgsqlConnection(IntegrationConfig.PostgresConnectionString!);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);

        var outcome = await Jobs.EnqueueAsync(tx, Step(lane: null), ct);
        await tx.CommitAsync(ct);

        Assert.Equal(JobEnqueueAction.Inserted, outcome.Action);
    }

    private JobEnqueueRequest Step(string? lane) =>
        new(TestNamespace, "lane-step", JobPayload.Json(new LaneStep(lane ?? "", "isolated")), Lane: lane);
}
