using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Execution;

/// <summary>
/// Conformance for <c>CompleteExecutionsBatch.Run</c>: the routine self-filters rows with a parent,
/// declines mismatched-lease rows, and aligns the returned <c>bool[]</c> to the original input
/// ordinals. Executing rows without a parent and with a matching lease are finalized: exclusive-key
/// rows included, since the key's lock is released C#-side, independent of the completion write; all
/// others are declined (caller must retry via the scalar path). Duplicate job ids in one batch are
/// legal (a stale attempt can be buffered alongside its reclaimed successor); correlation is by ordinal.
/// </summary>
[ConformanceSpec(
    "complete-executions-batch.self-filter",
    "CompleteExecutionsBatch self-filters and aligns outcomes to original ordinals",
    Area = "Execution",
    Contract = "CompleteExecutionsBatch finalizes plain Executing rows, declines parented or mismatched-lease rows, and accepts duplicate job ids, one bool per ordinal.",
    Arrange = "Plain, child, exclusive-key, and stale-lease jobs are enqueued and driven into Executing under a claimed lease.",
    Act = "CompleteExecutionsBatch runs over the Executing rows batched in interleaved order.",
    Assert = "The returned bool list aligns to the original ordinals, finalizing eligible rows and declining the rest, even when one job id appears twice."
)]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.CompleteExecutionsBatchAsync))]
public abstract class CompleteExecutionsBatchSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    // Large enough to never collide with a real execution_number (which starts at 1 and climbs slowly).
    private const int StaleOffset = 999;

    [Fact(
        DisplayName = "Mixed batch [plain,child,excl,plain,stale] returns exact [true,false,true,true,false] aligned to original ordinals"
    )]
    public async Task Mixed_batch_plain_child_excl_plain_stale_ordinals_are_exact()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect, leaseTtl, ns, workerId) = await DepsAsync(ct);

        if (dialect.Provider == DbProvider.Sqlite)
        {
            Assert.Skip("CompleteExecutionsBatch is not supported on SQLite (Bulk degrades to Direct there).");
        }

        // Seed: a non-terminal parent so the child enqueue is accepted, then the five probe jobs.
        var parentEnq = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(0, 0))),
            ct
        );
        var childEnq = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 1)), ParentJobId: parentEnq.JobId),
            ct
        );
        var exclEnq = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 2))) { ExclusiveKey = TestKey("excl-1") },
            ct
        );
        var plainAEnq = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(3, 3))),
            ct
        );
        var plainBEnq = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(4, 4))),
            ct
        );
        var staleEnq = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(5, 5))),
            ct
        );

        // Claim + start each probe into Executing (parentEnq stays Ready).
        var child = await ClaimAndStartAsync(db, dialect, ns, workerId, leaseTtl, childEnq.JobId, ct);
        var excl = await ClaimAndStartAsync(db, dialect, ns, workerId, leaseTtl, exclEnq.JobId, ct);
        var plainA = await ClaimAndStartAsync(db, dialect, ns, workerId, leaseTtl, plainAEnq.JobId, ct);
        var plainB = await ClaimAndStartAsync(db, dialect, ns, workerId, leaseTtl, plainBEnq.JobId, ct);
        var stale = await ClaimAndStartAsync(db, dialect, ns, workerId, leaseTtl, staleEnq.JobId, ct);

        // Build batch: ordinal [0]=plainA, [1]=child, [2]=excl, [3]=plainB, [4]=stale-wrong-exec-num.
        // Expected:          [true,          false,    true,     true,          false               ]
        // The keyed row finalizes: its lock is released C#-side, so the batch needs no key handling.
        var requests = new List<CompleteExecutionRequest>
        {
            MakeRequest(plainA, workerId),
            MakeRequest(child, workerId),
            MakeRequest(excl, workerId),
            MakeRequest(plainB, workerId),
            MakeRequest(stale, workerId, wrongExecutionNumber: true),
        };

        var results = await Services.GetRequiredService<IExecutionStore>().CompleteExecutionsBatchAsync(requests, ct);

        // Pin exact per-ordinal bool outcomes.
        Assert.Equal([true, false, true, true, false], results);

        // Pin post-state: true → Succeeded (100); false → still Executing (50).
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(plainAEnq.JobId, ct)).Status);
        Assert.Equal(JobStatusCode.Executing, (await ReadJobAsync(childEnq.JobId, ct)).Status);
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(exclEnq.JobId, ct)).Status);
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(plainBEnq.JobId, ct)).Status);
        Assert.Equal(JobStatusCode.Executing, (await ReadJobAsync(staleEnq.JobId, ct)).Status);
    }

    [Fact(
        DisplayName = "Second permutation [child,plain,stale,plain] returns exact [false,true,false,true] proving alignment is not positional luck"
    )]
    public async Task Second_permutation_child_plain_stale_plain_proves_ordinal_alignment()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect, leaseTtl, ns, workerId) = await DepsAsync(ct);

        if (dialect.Provider == DbProvider.Sqlite)
        {
            Assert.Skip("CompleteExecutionsBatch is not supported on SQLite (Bulk degrades to Direct there).");
        }

        var parentEnq = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(0, 0))),
            ct
        );
        var childEnq = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 1)), ParentJobId: parentEnq.JobId),
            ct
        );
        var plainAEnq = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 2))),
            ct
        );
        var staleEnq = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(3, 3))),
            ct
        );
        var plainBEnq = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(4, 4))),
            ct
        );

        var child = await ClaimAndStartAsync(db, dialect, ns, workerId, leaseTtl, childEnq.JobId, ct);
        var plainA = await ClaimAndStartAsync(db, dialect, ns, workerId, leaseTtl, plainAEnq.JobId, ct);
        var stale = await ClaimAndStartAsync(db, dialect, ns, workerId, leaseTtl, staleEnq.JobId, ct);
        var plainB = await ClaimAndStartAsync(db, dialect, ns, workerId, leaseTtl, plainBEnq.JobId, ct);

        // Batch: ordinal [0]=child, [1]=plainA, [2]=stale-wrong-exec-num, [3]=plainB.
        // Expected:          [false,   true,      false,                     true  ]
        var requests = new List<CompleteExecutionRequest>
        {
            MakeRequest(child, workerId),
            MakeRequest(plainA, workerId),
            MakeRequest(stale, workerId, wrongExecutionNumber: true),
            MakeRequest(plainB, workerId),
        };

        var results = await Services.GetRequiredService<IExecutionStore>().CompleteExecutionsBatchAsync(requests, ct);

        Assert.Equal([false, true, false, true], results);

        Assert.Equal(JobStatusCode.Executing, (await ReadJobAsync(childEnq.JobId, ct)).Status);
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(plainAEnq.JobId, ct)).Status);
        Assert.Equal(JobStatusCode.Executing, (await ReadJobAsync(staleEnq.JobId, ct)).Status);
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(plainBEnq.JobId, ct)).Status);
    }

    [Fact(DisplayName = "All-plain batch finalizes all rows and returns all-true")]
    public async Task All_plain_batch_finalizes_all_and_returns_all_true()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect, leaseTtl, ns, workerId) = await DepsAsync(ct);

        if (dialect.Provider == DbProvider.Sqlite)
        {
            Assert.Skip("CompleteExecutionsBatch is not supported on SQLite (Bulk degrades to Direct there).");
        }

        var enqA = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 1))), ct);
        var enqB = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 2))), ct);
        var enqC = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(3, 3))), ct);

        var clA = await ClaimAndStartAsync(db, dialect, ns, workerId, leaseTtl, enqA.JobId, ct);
        var clB = await ClaimAndStartAsync(db, dialect, ns, workerId, leaseTtl, enqB.JobId, ct);
        var clC = await ClaimAndStartAsync(db, dialect, ns, workerId, leaseTtl, enqC.JobId, ct);

        var requests = new List<CompleteExecutionRequest>
        {
            MakeRequest(clA, workerId),
            MakeRequest(clB, workerId),
            MakeRequest(clC, workerId),
        };

        var results = await Services.GetRequiredService<IExecutionStore>().CompleteExecutionsBatchAsync(requests, ct);

        Assert.Equal([true, true, true], results);

        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(enqA.JobId, ct)).Status);
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(enqB.JobId, ct)).Status);
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(enqC.JobId, ct)).Status);
    }

    [Fact(DisplayName = "Batch with a terminal failure row finalizes it as Failed and the event keeps the reason code")]
    public async Task Failure_row_with_reason_code_finalizes_and_event_keeps_reason()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect, leaseTtl, ns, workerId) = await DepsAsync(ct);

        if (dialect.Provider == DbProvider.Sqlite)
        {
            Assert.Skip("CompleteExecutionsBatch is not supported on SQLite (Bulk degrades to Direct there).");
        }

        var okEnq = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 1))), ct);
        var failEnq = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 2))),
            ct
        );

        var ok = await ClaimAndStartAsync(db, dialect, ns, workerId, leaseTtl, okEnq.JobId, ct);
        var fail = await ClaimAndStartAsync(db, dialect, ns, workerId, leaseTtl, failEnq.JobId, ct);

        // A terminal failure with an exhausted retry budget carries a non-null reason code through the
        // batch TVP; this is the payload shape the scalar path always handled but the batch never saw.
        var requests = new List<CompleteExecutionRequest>
        {
            MakeRequest(ok, workerId),
            MakeRequest(
                fail,
                workerId,
                outcome: ExecutionOutcome.Failed,
                reason: JobEventReasonCode.JobExecutionTimeout,
                reasonMessage: "Execution exceeded the configured timeout.",
                failureCount: 3
            ),
        };

        var results = await Services.GetRequiredService<IExecutionStore>().CompleteExecutionsBatchAsync(requests, ct);

        Assert.Equal([true, true], results);
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(okEnq.JobId, ct)).Status);
        Assert.Equal(JobStatusCode.Failed, (await ReadJobAsync(failEnq.JobId, ct)).Status);

        var events = await GetEventsByJobId.Run(Services, failEnq.JobId, ct);
        var finished = Assert.Single(events.Where(e => e.EventCode == EventCode.JobExecutionFinished));
        Assert.Equal(JobEventReasonCode.JobExecutionTimeout, finished.JobEventReasonCode);
        Assert.Equal(JobStatusCode.Failed, finished.ToStatus);
    }

    [Fact(DisplayName = "Duplicate job id in one batch: stale attempt declines, current attempt finalizes, unrelated row unaffected")]
    public async Task Duplicate_job_id_stale_declines_current_finalizes()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect, leaseTtl, ns, workerId) = await DepsAsync(ct);

        if (dialect.Provider == DbProvider.Sqlite)
        {
            Assert.Skip("CompleteExecutionsBatch is not supported on SQLite (Bulk degrades to Direct there).");
        }

        var aEnq = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 1))), ct);
        var bEnq = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 2))), ct);

        var a = await ClaimAndStartAsync(db, dialect, ns, workerId, leaseTtl, aEnq.JobId, ct);
        var b = await ClaimAndStartAsync(db, dialect, ns, workerId, leaseTtl, bEnq.JobId, ct);

        // A stale attempt for job A (mismatched execution number) buffered alongside the current one:
        // exactly the overlap the runtime produces when a reclaimed job is re-dispatched in-process
        // while the previous attempt is still unwinding. Both rows share one job id in one batch.
        var requests = new List<CompleteExecutionRequest>
        {
            MakeRequest(a, workerId, wrongExecutionNumber: true),
            MakeRequest(a, workerId),
            MakeRequest(b, workerId),
        };

        var results = await Services.GetRequiredService<IExecutionStore>().CompleteExecutionsBatchAsync(requests, ct);

        Assert.Equal([false, true, true], results);
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(aEnq.JobId, ct)).Status);
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(bEnq.JobId, ct)).Status);

        // Exactly one finished event for job A: the stale request must not fan out through the
        // ordinal correlation.
        var events = await GetEventsByJobId.Run(Services, aEnq.JobId, ct);
        Assert.Single(events.Where(e => e.EventCode == EventCode.JobExecutionFinished));
    }

    [Fact(DisplayName = "Wrong-owner batch entry declines with false and scalar CompleteExecution returns NotOwner")]
    public async Task Wrong_owner_batch_entry_declines_and_scalar_complete_returns_not_owner()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect, leaseTtl, ns, workerId) = await DepsAsync(ct);

        if (dialect.Provider == DbProvider.Sqlite)
        {
            Assert.Skip("CompleteExecutionsBatch is not supported on SQLite (Bulk degrades to Direct there).");
        }

        var enq = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(10, 20))), ct);
        var claimed = await ClaimAndStartAsync(db, dialect, ns, workerId, leaseTtl, enq.JobId, ct);

        // A request carrying a different (non-existent) worker id acts as a lease mismatch.
        var fakeWorkerId = -workerId;
        var wrongOwnerRequest = MakeRequest(claimed, fakeWorkerId);

        // Batch declines: the runtime row's leased_by_worker_id is workerId but the request says fakeWorkerId.
        var results = await Services.GetRequiredService<IExecutionStore>().CompleteExecutionsBatchAsync([wrongOwnerRequest], ct);
        Assert.Equal([false], results);

        // Job must still be Executing: the batch left it untouched.
        Assert.Equal(JobStatusCode.Executing, (await ReadJobAsync(enq.JobId, ct)).Status);

        // Scalar path with the same fake worker: the ownership guard sees a different
        // leased_by_worker_id → NotOwner, proving the batch correctly declined an unowned row.
        var scalar = await Services.GetRequiredService<IExecutionStore>().CompleteExecutionAsync(wrongOwnerRequest, ct);
        Assert.Equal(CompleteExecutionAction.NotOwner, scalar.Action);
    }

    // ---------- helpers ----------

    private async Task<(IDbSession Db, ISqlDialect Dialect, int LeaseTtl, short Ns, int WorkerId)> DepsAsync(CancellationToken ct)
    {
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var dialect = Services.GetRequiredService<ISqlDialect>();
        var leaseTtl = Services.GetRequiredService<IOptions<JobsOptions>>().Value.LeaseTtlSeconds;
        var worker = await Db.From<JobWorker>().Where(w => w.NamespaceId == ns).SingleOrDefaultAsync(ct);
        Assert.NotNull(worker);
        return (Db, dialect, leaseTtl, ns, worker!.Id);
    }

    private async Task<ClaimedJob> ClaimAndStartAsync(
        IDbSession db,
        ISqlDialect dialect,
        short ns,
        int workerId,
        int leaseTtl,
        long jobId,
        CancellationToken ct
    )
    {
        var claimed = Assert.Single(await Services.GetRequiredService<IExecutionStore>().ClaimOneAsync(ns, workerId, leaseTtl, jobId, ct));
        Assert.Equal(
            StartExecutionAction.Started,
            await Services
                .GetRequiredService<IExecutionStore>()
                .StartExecutionAsync(claimed.JobId, workerId, claimed.ExecutionNumber, claimed.Version, leaseTtl, ct)
        );
        return claimed;
    }

    private static CompleteExecutionRequest MakeRequest(
        ClaimedJob claimed,
        int workerId,
        bool wrongExecutionNumber = false,
        ExecutionOutcome outcome = ExecutionOutcome.Succeeded,
        JobEventReasonCode? reason = null,
        string? reasonMessage = null,
        short? failureCount = null
    ) =>
        new(
            JobId: claimed.JobId,
            WorkerId: workerId,
            ExpectedExecutionNumber: claimed.ExecutionNumber + (wrongExecutionNumber ? StaleOffset : 0),
            Outcome: outcome,
            ResultFormatId: 0,
            Result: ReadOnlyMemory<byte>.Empty,
            JobEventReasonCode: reason,
            ReasonMessage: reasonMessage,
            FailureCount: failureCount
        );
}
