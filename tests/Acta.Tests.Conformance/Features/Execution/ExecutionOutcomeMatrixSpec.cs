using Acta.Features.Execution;
using Acta.Payloads;
using Acta.Relational.Entities;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Execution;

/// <summary>
/// Covers the unexercised outcome branches of <c>StartExecution</c> and <c>CompleteExecution</c>:
/// NotOwner and AlreadyTerminal on both operations. No-op outcomes must write no event.
/// </summary>
[ConformanceSpec(
    "execution.outcome-matrix",
    "StartExecution and CompleteExecution no-op outcomes return exact action enums",
    Area = "Execution",
    Contract = "No-op StartExecution and CompleteExecution outcomes (wrong owner, already-terminal) never emit events and return the exact discriminated action.",
    Arrange = "Enqueued jobs are claimed and driven into owned, terminal, and displaced states.",
    Act = "StartExecution and CompleteExecution are invoked with a wrong worker, on terminal jobs, as a double complete, and from a displaced worker.",
    Assert = "Each no-op path returns its exact action such as NotOwner or AlreadyTerminal and writes no new event."
)]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.StartExecutionAsync))]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.CompleteExecutionAsync))]
public abstract class ExecutionOutcomeMatrixSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private const int LiveLeaseTtl = 30;

    // ---------- StartExecution outcomes ----------

    [Fact(DisplayName = "StartExecution with wrong worker returns NotOwner and writes no job.execution.started event")]
    public async Task Start_wrong_worker_returns_not_owner_and_no_event()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect, ns, workerId) = await DepsAsync(ct);

        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 2))),
            ct
        );
        var claimed = Assert.Single(
            await Services.GetRequiredService<IExecutionStore>().ClaimOneAsync(ns, workerId, LiveLeaseTtl, enqueued, ct)
        );

        // Wrong worker: correct execution_number+version but different worker id.
        var action = await Services
            .GetRequiredService<IExecutionStore>()
            .StartExecutionAsync(enqueued.JobId, -workerId, claimed.ExecutionNumber, claimed.Version, LiveLeaseTtl, ct);

        Assert.Equal(StartExecutionAction.NotOwner, action);
        Assert.Equal(JobStatusCode.Dispatched, (await ReadJobAsync(enqueued.JobId, ct)).Status);
        Assert.Equal(0, await CountEventsAsync(enqueued.JobId, JobEventCode.JobExecutionStarted, ct));
    }

    [Fact(DisplayName = "StartExecution on a terminal job returns AlreadyTerminal and writes no additional started event")]
    public async Task Start_terminal_job_returns_already_terminal_and_no_additional_event()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect, ns, workerId) = await DepsAsync(ct);

        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(3, 4))),
            ct
        );

        // Drive to Done: claim → start → complete.
        var claimed = Assert.Single(
            await Services.GetRequiredService<IExecutionStore>().ClaimOneAsync(ns, workerId, LiveLeaseTtl, enqueued, ct)
        );
        Assert.Equal(
            StartExecutionAction.Started,
            await Services
                .GetRequiredService<IExecutionStore>()
                .StartExecutionAsync(enqueued.JobId, workerId, claimed.ExecutionNumber, claimed.Version, LiveLeaseTtl, ct)
        );
        Assert.Equal(
            CompleteExecutionAction.Completed,
            (await Services.GetRequiredService<IExecutionStore>().CompleteExecutionAsync(MakeCompleteRequest(claimed, workerId), ct)).Action
        );

        // Exactly one started event written during the successful execution.
        Assert.Equal(1, await CountEventsAsync(enqueued.JobId, JobEventCode.JobExecutionStarted, ct));

        // Retry start with stale claim: SQL checks status IN (100, 200, 220) → AlreadyTerminal before NotOwner.
        var action = await Services
            .GetRequiredService<IExecutionStore>()
            .StartExecutionAsync(enqueued.JobId, workerId, claimed.ExecutionNumber, claimed.Version, LiveLeaseTtl, ct);

        Assert.Equal(StartExecutionAction.AlreadyTerminal, action);
        Assert.Equal(JobStatusCode.Done, (await ReadJobAsync(enqueued.JobId, ct)).Status);
        // Count must remain at 1 — no second started event written by the no-op.
        Assert.Equal(1, await CountEventsAsync(enqueued.JobId, JobEventCode.JobExecutionStarted, ct));
    }

    // ---------- CompleteExecution outcomes ----------

    [Fact(DisplayName = "CompleteExecution with wrong worker returns NotOwner and writes no job.execution.finished event")]
    public async Task Complete_wrong_worker_returns_not_owner_and_no_event()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect, ns, workerId) = await DepsAsync(ct);

        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(5, 6))),
            ct
        );

        var claimed = Assert.Single(
            await Services.GetRequiredService<IExecutionStore>().ClaimOneAsync(ns, workerId, LiveLeaseTtl, enqueued, ct)
        );
        Assert.Equal(
            StartExecutionAction.Started,
            await Services
                .GetRequiredService<IExecutionStore>()
                .StartExecutionAsync(enqueued.JobId, workerId, claimed.ExecutionNumber, claimed.Version, LiveLeaseTtl, ct)
        );

        // Wrong worker: the runtime row's leased_by_worker_id = workerId, request says a foreign id.
        var result = await Services
            .GetRequiredService<IExecutionStore>()
            .CompleteExecutionAsync(MakeCompleteRequest(claimed, -workerId), ct);

        Assert.Equal(CompleteExecutionAction.NotOwner, result.Action);
        Assert.Equal(JobStatusCode.Executing, (await ReadJobAsync(enqueued.JobId, ct)).Status);
        Assert.Equal(0, await CountEventsAsync(enqueued.JobId, JobEventCode.JobExecutionFinished, ct));
    }

    [Fact(DisplayName = "Second CompleteExecution on a terminal job returns AlreadyTerminal with no additional finished event")]
    public async Task Complete_double_returns_already_terminal_and_no_additional_event()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect, ns, workerId) = await DepsAsync(ct);

        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(7, 8))),
            ct
        );

        var claimed = Assert.Single(
            await Services.GetRequiredService<IExecutionStore>().ClaimOneAsync(ns, workerId, LiveLeaseTtl, enqueued, ct)
        );
        Assert.Equal(
            StartExecutionAction.Started,
            await Services
                .GetRequiredService<IExecutionStore>()
                .StartExecutionAsync(enqueued.JobId, workerId, claimed.ExecutionNumber, claimed.Version, LiveLeaseTtl, ct)
        );
        var req = MakeCompleteRequest(claimed, workerId);
        Assert.Equal(
            CompleteExecutionAction.Completed,
            (await Services.GetRequiredService<IExecutionStore>().CompleteExecutionAsync(req, ct)).Action
        );

        // Baseline: Done with exactly one finished event.
        Assert.Equal(JobStatusCode.Done, (await ReadJobAsync(enqueued.JobId, ct)).Status);
        var finishedBefore = await CountEventsAsync(enqueued.JobId, JobEventCode.JobExecutionFinished, ct);
        Assert.Equal(1, finishedBefore);

        // Second complete — same worker and execution_number.
        var result2 = await Services.GetRequiredService<IExecutionStore>().CompleteExecutionAsync(req, ct);

        Assert.Equal(CompleteExecutionAction.AlreadyTerminal, result2.Action);
        Assert.Equal(JobStatusCode.Done, (await ReadJobAsync(enqueued.JobId, ct)).Status);
        // Event count must be unchanged — no second finished event.
        Assert.Equal(finishedBefore, await CountEventsAsync(enqueued.JobId, JobEventCode.JobExecutionFinished, ct));
    }

    [Fact(DisplayName = "Stale CompleteExecution by a displaced worker returns NotOwner and leaves job owned by the new claimant")]
    public async Task Stolen_row_stale_complete_returns_not_owner_and_job_stays_dispatched()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect, ns, workerId) = await DepsAsync(ct);

        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(9, 10))),
            ct
        );

        // Worker1 claims and starts → Executing(50), execution_number=1.
        var worker1Id = workerId;
        var claimed1 = Assert.Single(
            await Services.GetRequiredService<IExecutionStore>().ClaimOneAsync(ns, worker1Id, LiveLeaseTtl, enqueued, ct)
        );
        Assert.Equal(
            StartExecutionAction.Started,
            await Services
                .GetRequiredService<IExecutionStore>()
                .StartExecutionAsync(enqueued.JobId, worker1Id, claimed1.ExecutionNumber, claimed1.Version, LiveLeaseTtl, ct)
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
            await Services.GetRequiredService<IExecutionStore>().ClaimOneAsync(ns, worker2Id, LiveLeaseTtl, enqueued, ct)
        );
        Assert.Equal(claimed1.ExecutionNumber + 1, claimed2.ExecutionNumber);

        // Capture finished-event count before the stale attempt (one orphaned event exists from the reclaim).
        var finishedBefore = await CountEventsAsync(enqueued.JobId, JobEventCode.JobExecutionFinished, ct);
        Assert.Equal(1, finishedBefore);

        // Worker1's stale complete: (worker1Id, execution_number=1) vs. current (worker2Id, execution_number=2, status=40).
        // SQL: status=Dispatched(40) is not terminal, leased_by_worker_id=worker2Id ≠ worker1Id → NotOwner.
        var result = await Services
            .GetRequiredService<IExecutionStore>()
            .CompleteExecutionAsync(MakeCompleteRequest(claimed1, worker1Id), ct);

        Assert.Equal(CompleteExecutionAction.NotOwner, result.Action);
        Assert.Equal(JobStatusCode.Dispatched, (await ReadJobAsync(enqueued.JobId, ct)).Status);
        // Count unchanged — the stale attempt wrote no additional finished event.
        Assert.Equal(finishedBefore, await CountEventsAsync(enqueued.JobId, JobEventCode.JobExecutionFinished, ct));
    }

    // ---------- helpers ----------

    private async Task<(IDbSession Db, ISqlDialect Dialect, short Ns, int WorkerId)> DepsAsync(CancellationToken ct)
    {
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var dialect = Services.GetRequiredService<ISqlDialect>();
        var worker = await Db.From<JobWorker>().Where(w => w.NamespaceId == ns).SingleOrDefaultAsync(ct);
        Assert.NotNull(worker);
        return (Db, dialect, ns, worker!.Id);
    }

    private static CompleteExecutionRequest MakeCompleteRequest(ClaimedJob claimed, int workerId) =>
        new(
            JobId: claimed.JobId,
            WorkerId: workerId,
            ExpectedExecutionNumber: claimed.ExecutionNumber,
            Outcome: ExecutionOutcome.Succeeded,
            ResultFormatId: 0,
            Result: ReadOnlyMemory<byte>.Empty
        );
}
