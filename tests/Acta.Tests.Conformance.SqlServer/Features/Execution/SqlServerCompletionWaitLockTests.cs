using System.Data.Common;
using Acta.Tests.Conformance.SqlServer.Testing;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.SqlServer.Features.Execution;

/// <summary>
/// SQL Server <c>complete_executions</c> takes a waiting job's awaited slot lock, with every other slot lock it needs,
/// before it reads any slot, as every writer of a slot does: it holds no gap of <c>pk_checkpoints</c>, and a raise that
/// creates the awaited slot still waits for the landing and then wakes the job.
/// </summary>
public sealed class SqlServerCompletionWaitLockTests : ActaRuntimeTestBase<SqlServerConformanceFixture, TestJobsManifest>
{
    private SignalsOperations Slots => Services.GetRequiredService<SignalsOperations>();

    [Fact(DisplayName = "A wait landing held open leaves the key gap after its awaited slot free for another slot's insert")]
    public async Task Held_wait_landing_does_not_lock_the_gap_after_the_awaited_slot()
    {
        var ct = TestContext.Current.CancellationToken;
        var (workerId, claimed) = await ClaimAsync(ct);
        var wait = await Slots.WaitSignalAsync(
            new ExecutionFence(claimed.JobId, claimed.ExecutionNumber, workerId),
            CheckpointKindCode.Signal,
            "go",
            null,
            null,
            ct
        );
        Assert.Equal(WaitSignalOutcomeCode.SuspendPending, wait.Outcome);

        await using var holder = await Db.OpenConnectionAsync(ct);
        await using var hold = await holder.BeginTransactionAsync(ct);
        await LandWaitingAsync(hold, workerId, claimed, ct);

        // ('gp') sorts right after the awaited name under the latch kind, inside the gap a range lock would hold. The
        // insert is a bare one: a handler's own wait would rightly wait out the landing on the job's row.
        var insert = Db.ExecuteRawAsync(
            "INSERT INTO {schema}.checkpoints (job_id, checkpoint_kind_code, checkpoint_name, checkpoint_status_code, value_format_id)"
                + " VALUES (@p_job_id, @p_kind, @p_name, @p_status, 0)",
            ct,
            ("@p_job_id", claimed.JobId),
            ("@p_kind", (byte)CheckpointKindCode.ChildLatch),
            ("@p_name", "gp"),
            ("@p_status", (byte)CheckpointStatusCode.Pending)
        );
        var first = await Task.WhenAny(insert, Task.Delay(TimeSpan.FromSeconds(5), ct));
        await hold.RollbackAsync(ct);

        Assert.True(first == insert, "the slot insert waited on the open landing");
        Assert.Equal(1, await insert);
    }

    [Fact(DisplayName = "A raise that creates a missing awaited slot waits for the wait landing, then wakes the job")]
    public async Task Raise_creating_the_awaited_slot_waits_for_the_landing_and_wakes_the_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var (workerId, claimed) = await ClaimAsync(ct);

        await using var holder = await Db.OpenConnectionAsync(ct);
        await using var hold = await holder.BeginTransactionAsync(ct);
        await LandWaitingAsync(hold, workerId, claimed, ct);

        var input = new JobControlInput("op", null, null);
        var raise = Slots.RaiseSignalAsync(new RaiseSignalSlot(claimed.JobId, CheckpointKindCode.Signal, "go", 0, null, input), ct);
        var first = await Task.WhenAny(raise, Task.Delay(TimeSpan.FromSeconds(1), ct));
        await hold.CommitAsync(ct);

        Assert.True(first != raise, "the raise did not wait for the open landing");
        Assert.Equal(JobStatusCode.Ready, (await raise).Status);
    }

    private async Task<(int WorkerId, ClaimedJob Claimed)> ClaimAsync(CancellationToken ct)
    {
        var (_, workerId) = await WorkerTestOps.StartAsync(
            Services,
            TestNamespace,
            "test",
            null,
            "wait-lock-host",
            "v1",
            null,
            null,
            4244,
            4,
            ct
        );
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-wait-signal", JobPayload.None), ct);
        var claimed = Assert.Single(
            (
                await Services
                    .GetRequiredService<ExecutionOperations>()
                    .ClaimJobAsync(
                        new ClaimOptions(Runtime.RegisteredNamespaceIds[TestNamespace], workerId, MaxBatch: 1),
                        30,
                        enqueued.JobId,
                        ct
                    )
            ).Jobs
        );
        return (workerId, claimed);
    }

    /// <summary>Lands <paramref name="claimed"/> waiting on the signal "go" inside <paramref name="transaction"/>, left open.</summary>
    private async Task LandWaitingAsync(DbTransaction transaction, int workerId, ClaimedJob claimed, CancellationToken ct)
    {
        var dialect = Services.GetRequiredService<ISqlDialect>();
        var command = new CompleteExecutionsCommand(workerId, [CompletionTestOps.Landing(claimed) with { WaitSignalName = "go" }]);
        var rows = await Db.ExecuteInTransactionAsync(
            transaction,
            new StoreCommand("CompleteExecutions"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeasedByWorkerId, workerId));
                dialect.BindCompleteExecutions(cmd, command, Db.Schema);
            },
            DbProjectionResolver.Resolve<CompleteExecutionsRow>(),
            ct
        );
        Assert.Equal(CompleteExecutionsAction.Completed, Assert.Single(rows).Action);
    }
}
