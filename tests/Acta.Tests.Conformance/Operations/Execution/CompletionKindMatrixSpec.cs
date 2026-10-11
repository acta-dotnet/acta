using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Execution;

/// <summary>
/// Covers the unexercised outcome branches of <c>StartExecution</c> and <c>CompleteExecutions</c>:
/// NotOwner and AlreadyTerminal on both operations. No-op outcomes must write no event.
/// </summary>
[ConformanceSpec(
    "execution.outcome-matrix",
    "StartExecution and CompleteExecutions no-op outcomes return exact action enums",
    Area = "Execution",
    Contract = "No-op StartExecution and CompleteExecutions outcomes (wrong owner, already-terminal) never emit events and return the exact discriminated action."
)]
public sealed class CompletionKindMatrixSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    private const int LiveLeaseTtl = 30;

    [Fact(DisplayName = "StartExecution with wrong worker returns NotOwner and writes no job.execution-started event")]
    public async Task Start_wrong_worker_returns_not_owner_and_no_event()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, ns, workerId) = await DepsAsync(ct);

        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 2))),
            ct
        );
        var claimed = Assert.Single(
            await Services.GetRequiredService<ExecutionOperations>().ClaimJobAsync(ns, workerId, LiveLeaseTtl, enqueued, ct)
        );

        // Wrong worker: correct execution_number+version but different worker id.
        var action = (
            await Services
                .GetRequiredService<ExecutionOperations>()
                .StartExecutionAsync(enqueued.JobId, -workerId, claimed.ExecutionNumber, LiveLeaseTtl, ct)
        ).Action;

        Assert.Equal(StartExecutionAction.NotOwner, action);
        Assert.Equal(JobStatusCode.Dispatched, (await ReadJobAsync(enqueued.JobId, ct)).Status);
        Assert.Equal(0, await CountEventsAsync(enqueued.JobId, EventCode.JobExecutionStarted, ct));
    }

    [Fact(DisplayName = "StartExecution on a terminal job returns AlreadyTerminal and writes no additional started event")]
    public async Task Start_terminal_job_returns_already_terminal_and_no_additional_event()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, ns, workerId) = await DepsAsync(ct);

        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(3, 4))),
            ct
        );

        // Drive to Succeeded: claim → start → complete.
        var claimed = Assert.Single(
            await Services.GetRequiredService<ExecutionOperations>().ClaimJobAsync(ns, workerId, LiveLeaseTtl, enqueued, ct)
        );
        Assert.Equal(
            StartExecutionAction.Started,
            (
                await Services
                    .GetRequiredService<ExecutionOperations>()
                    .StartExecutionAsync(enqueued.JobId, workerId, claimed.ExecutionNumber, LiveLeaseTtl, ct)
            ).Action
        );
        Assert.Equal(
            CompleteExecutionsAction.Completed,
            (
                await Services.GetRequiredService<ExecutionOperations>().CompleteAsync(workerId, CompletionTestOps.Landing(claimed), ct)
            ).Action
        );

        // Exactly one started event written during the successful execution.
        Assert.Equal(1, await CountEventsAsync(enqueued.JobId, EventCode.JobExecutionStarted, ct));

        // Retry start with stale claim: SQL checks status IN (100, 200, 220) → AlreadyTerminal before NotOwner.
        var action = (
            await Services
                .GetRequiredService<ExecutionOperations>()
                .StartExecutionAsync(enqueued.JobId, workerId, claimed.ExecutionNumber, LiveLeaseTtl, ct)
        ).Action;

        Assert.Equal(StartExecutionAction.AlreadyTerminal, action);
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(enqueued.JobId, ct)).Status);
        // Count must remain at 1: no second started event written by the no-op.
        Assert.Equal(1, await CountEventsAsync(enqueued.JobId, EventCode.JobExecutionStarted, ct));
    }

    [Fact(DisplayName = "CompleteExecutions with wrong worker returns NotOwner and writes no job.execution-finished event")]
    public async Task Complete_wrong_worker_returns_not_owner_and_no_event()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, ns, workerId) = await DepsAsync(ct);

        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(5, 6))),
            ct
        );

        var claimed = Assert.Single(
            await Services.GetRequiredService<ExecutionOperations>().ClaimJobAsync(ns, workerId, LiveLeaseTtl, enqueued, ct)
        );
        Assert.Equal(
            StartExecutionAction.Started,
            (
                await Services
                    .GetRequiredService<ExecutionOperations>()
                    .StartExecutionAsync(enqueued.JobId, workerId, claimed.ExecutionNumber, LiveLeaseTtl, ct)
            ).Action
        );

        // Wrong worker: the runtime row's leased_by_worker_id = workerId, request says a foreign id.
        var result = await Services
            .GetRequiredService<ExecutionOperations>()
            .CompleteAsync(-workerId, CompletionTestOps.Landing(claimed), ct);

        Assert.Equal(CompleteExecutionsAction.NotOwner, result.Action);
        Assert.Equal(JobStatusCode.Executing, (await ReadJobAsync(enqueued.JobId, ct)).Status);
        Assert.Equal(0, await CountEventsAsync(enqueued.JobId, EventCode.JobExecutionFinished, ct));
    }

    [Fact(DisplayName = "Second CompleteExecutions on a terminal job returns AlreadyTerminal with no additional finished event")]
    public async Task Complete_double_returns_already_terminal_and_no_additional_event()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, ns, workerId) = await DepsAsync(ct);

        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(7, 8))),
            ct
        );

        var claimed = Assert.Single(
            await Services.GetRequiredService<ExecutionOperations>().ClaimJobAsync(ns, workerId, LiveLeaseTtl, enqueued, ct)
        );
        Assert.Equal(
            StartExecutionAction.Started,
            (
                await Services
                    .GetRequiredService<ExecutionOperations>()
                    .StartExecutionAsync(enqueued.JobId, workerId, claimed.ExecutionNumber, LiveLeaseTtl, ct)
            ).Action
        );
        var req = CompletionTestOps.Landing(claimed);
        Assert.Equal(
            CompleteExecutionsAction.Completed,
            (await Services.GetRequiredService<ExecutionOperations>().CompleteAsync(workerId, req, ct)).Action
        );

        // Baseline: Succeeded with exactly one finished event.
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(enqueued.JobId, ct)).Status);
        var finishedBefore = await CountEventsAsync(enqueued.JobId, EventCode.JobExecutionFinished, ct);
        Assert.Equal(1, finishedBefore);

        // Second complete: same worker and execution_number.
        var result2 = await Services.GetRequiredService<ExecutionOperations>().CompleteAsync(workerId, req, ct);

        Assert.Equal(CompleteExecutionsAction.AlreadyTerminal, result2.Action);
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(enqueued.JobId, ct)).Status);
        // Event count must be unchanged: no second finished event.
        Assert.Equal(finishedBefore, await CountEventsAsync(enqueued.JobId, EventCode.JobExecutionFinished, ct));
    }

    [Fact(DisplayName = "Stale CompleteExecutions by a displaced worker returns NotOwner and leaves job owned by the new claimant")]
    public async Task Stolen_row_stale_complete_returns_not_owner_and_job_stays_dispatched()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, _, ns, workerId) = await DepsAsync(ct);

        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(9, 10))),
            ct
        );

        // Worker1 claims and starts → Executing(50), execution_number=1.
        var worker1Id = workerId;
        var claimed1 = Assert.Single(
            await Services.GetRequiredService<ExecutionOperations>().ClaimJobAsync(ns, worker1Id, LiveLeaseTtl, enqueued, ct)
        );
        Assert.Equal(
            StartExecutionAction.Started,
            (
                await Services
                    .GetRequiredService<ExecutionOperations>()
                    .StartExecutionAsync(enqueued.JobId, worker1Id, claimed1.ExecutionNumber, LiveLeaseTtl, ct)
            ).Action
        );

        // Expire the lease so the Executing row is eligible for reclaim.
        await ChaosSpecHelpers.ExpireLeaseAsync(db, enqueued.JobId, ct);

        // Reclaim: job → Ready, failure_count+1, orphaned finished event written.
        Assert.Equal(1, (await RecoverySweep.ReclaimAtLeastOneAsync(Services, ns, ct)).Reclaimed);

        // Worker2 (a different id) claims the now-Ready job → Dispatched(40), execution_number=2.
        // Negative so the abandoned lease can never collide with a real future worker's identity id:
        // this row stays Dispatched forever in the append-only test DB, and a positive fabricated id
        // eventually matches a later run's worker, whose heartbeat then extends this foreign lease.
        var worker2Id = -workerId;
        var claimed2 = Assert.Single(
            await Services.GetRequiredService<ExecutionOperations>().ClaimJobAsync(ns, worker2Id, LiveLeaseTtl, enqueued, ct)
        );
        Assert.Equal(claimed1.ExecutionNumber + 1, claimed2.ExecutionNumber);

        // Capture finished-event count before the stale attempt (one orphaned event exists from the reclaim).
        var finishedBefore = await CountEventsAsync(enqueued.JobId, EventCode.JobExecutionFinished, ct);
        Assert.Equal(1, finishedBefore);

        // Worker1's stale complete: (worker1Id, execution_number=1) vs. current (worker2Id, execution_number=2, status=40).
        // SQL: status=Dispatched(40) is not terminal, leased_by_worker_id=worker2Id ≠ worker1Id → NotOwner.
        var result = await Services
            .GetRequiredService<ExecutionOperations>()
            .CompleteAsync(worker1Id, CompletionTestOps.Landing(claimed1), ct);

        Assert.Equal(CompleteExecutionsAction.NotOwner, result.Action);
        Assert.Equal(JobStatusCode.Dispatched, (await ReadJobAsync(enqueued.JobId, ct)).Status);
        // Count unchanged: the stale attempt wrote no additional finished event.
        Assert.Equal(finishedBefore, await CountEventsAsync(enqueued.JobId, EventCode.JobExecutionFinished, ct));
    }

    private async Task<(IDbSession Db, ISqlDialect Dialect, int Ns, int WorkerId)> DepsAsync(CancellationToken ct)
    {
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var dialect = Services.GetRequiredService<ISqlDialect>();
        var worker = await Db.From<WorkerEntity>().Where(w => w.NamespaceId == ns).SingleOrDefaultAsync(ct);
        Assert.NotNull(worker);
        return (Db, dialect, ns, worker!.Id);
    }
}
