using System.Collections.Concurrent;
using Acta.Configuration;
using Acta.Features.Execution;
using Acta.Features.Workers;
using Acta.Payloads;
using Acta.Relational.Entities;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

/// <summary>
/// Proves <see cref="CompletionSink"/> fallback path applies full completion semantics. Rows the
/// set-based <c>CompleteExecutionsBatch</c> self-filters (a parent) fall back to scalar
/// <c>CompleteExecution</c>, which must flip a Suspended parent to Ready and emit lifecycle events
/// that match scalar completion. Plain rows finalized by the batch are also pinned (guard). On
/// SQLite, Bulk degrades to Direct (no batch routine), so all facts skip at runtime.
/// </summary>
[ConformanceSpec(
    "completion-sink.bulk-fallback",
    "CompletionSink fallback path applies full completion semantics",
    Area = "Execution",
    Contract = "Sink fallback (batch self-filter) applies full completion semantics: parent latch flip and lifecycle events matching scalar CompleteExecution.",
    Arrange = "A Suspended parent with a running child and a plain job exist under ExecutionProfile.Bulk.",
    Act = "The child completes through the sink's scalar fallback while the plain job finalizes via CompleteExecutionsBatch.",
    Assert = "The fallback flips the Suspended parent to Ready with lifecycle events matching scalar completion, and the plain row finalizes with exact statuses."
)]
public abstract class CompletionSinkBulkFallbackSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "Parent latch flip via fallback: child Done via sink releases Suspended parent to Ready")]
    public async Task Parent_latch_flip_via_fallback()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect, leaseTtl, ns, workerId) = await DepsAsync(ct);

        if (dialect.Provider == DbProvider.Sqlite)
        {
            Assert.Skip("CompletionSink calls CompleteExecutionsBatch which is not supported on SQLite.");
        }

        // Run the parent so it suspends waiting for its child latch.
        var parentEnq = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-parent-one", JobPayload.None), ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(parentEnq, ct));
        Assert.Equal(JobStatusCode.Suspended, (await ReadJobAsync(parentEnq.JobId, ct)).Status);

        // The parent handler created the child; claim + start it to Executing.
        var child = Assert.Single(await ReadChildrenAsync(parentEnq.JobId, ct));
        var claimedChild = Assert.Single(
            await Services.GetRequiredService<IExecutionStore>().ClaimOneAsync(ns, workerId, leaseTtl, child.Id, ct)
        );
        Assert.Equal(
            StartExecutionAction.Started,
            await Services
                .GetRequiredService<IExecutionStore>()
                .StartExecutionAsync(claimedChild.JobId, workerId, claimedChild.ExecutionNumber, claimedChild.Version, leaseTtl, ct)
        );

        // Drive through the sink (child has a parent → batch self-filters → fallback scalar path).
        var spy = new WakeupSpy();
        var sink = MakeSink(spy);
        await sink.EnqueueAsync(new BufferedCompletion(MakeRequest(claimedChild, workerId), TestNamespace, child.Id, 0));
        sink.CompleteWriter();
        await sink.RunFlusherAsync();

        // Primary assertions: DB state is the source of truth.
        Assert.Equal(JobStatusCode.Done, (await ReadJobAsync(child.Id, ct)).Status);
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(parentEnq.JobId, ct)).Status);

        // Secondary: AllWorkerNamespaces/WorkAvailable wake fired (ParentReleased).
        Assert.Contains(
            spy.Wakes,
            w => w.Channel.Kind == WorkerWakeupChannelKind.AllWorkerNamespaces && w.Reason == WorkerWakeupReason.WorkAvailable
        );
    }

    [Fact(
        DisplayName = "Fallback equals scalar parity: child completion via sink emits exact Done status and Succeeded JobExecutionFinished event"
    )]
    public async Task Fallback_equals_scalar_parity()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect, leaseTtl, ns, workerId) = await DepsAsync(ct);

        if (dialect.Provider == DbProvider.Sqlite)
        {
            Assert.Skip("CompletionSink calls CompleteExecutionsBatch which is not supported on SQLite.");
        }

        // Same seed as the parent-latch test: run parent → suspend → claim+start child → sink.
        var parentEnq = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-parent-one", JobPayload.None), ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(parentEnq, ct));
        var child = Assert.Single(await ReadChildrenAsync(parentEnq.JobId, ct));

        var claimed = Assert.Single(
            await Services.GetRequiredService<IExecutionStore>().ClaimOneAsync(ns, workerId, leaseTtl, child.Id, ct)
        );
        Assert.Equal(
            StartExecutionAction.Started,
            await Services
                .GetRequiredService<IExecutionStore>()
                .StartExecutionAsync(claimed.JobId, workerId, claimed.ExecutionNumber, claimed.Version, leaseTtl, ct)
        );

        var sink = MakeSink(new WakeupSpy());
        await sink.EnqueueAsync(new BufferedCompletion(MakeRequest(claimed, workerId), TestNamespace, child.Id, 0));
        sink.CompleteWriter();
        await sink.RunFlusherAsync();

        // Exact terminal status: same as scalar CompleteExecution.Run would produce.
        Assert.Equal(JobStatusCode.Done, (await ReadJobAsync(child.Id, ct)).Status);

        // Exact lifecycle event: one JobExecutionFinished with ExecutionStatus Succeeded.
        var evt = await ReadSingleEventAsync(child.Id, JobEventCode.JobExecutionFinished, ct);
        Assert.Equal(ExecutionStatusCode.Succeeded, evt.ExecutionStatus);
    }

    [Fact(DisplayName = "Plain row finalized by batch (guard): plain job reaches Done via batch path and JobFinished wake fires")]
    public async Task Plain_row_finalized_by_batch_guard()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect, leaseTtl, ns, workerId) = await DepsAsync(ct);

        if (dialect.Provider == DbProvider.Sqlite)
        {
            Assert.Skip("CompletionSink calls CompleteExecutionsBatch which is not supported on SQLite.");
        }

        // Plain job: no parent, no exclusive key → batch finalizes it directly.
        var enq = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(3, 4))), ct);
        var claimed = Assert.Single(
            await Services.GetRequiredService<IExecutionStore>().ClaimOneAsync(ns, workerId, leaseTtl, enq.JobId, ct)
        );
        Assert.Equal(
            StartExecutionAction.Started,
            await Services
                .GetRequiredService<IExecutionStore>()
                .StartExecutionAsync(claimed.JobId, workerId, claimed.ExecutionNumber, claimed.Version, leaseTtl, ct)
        );

        var spy = new WakeupSpy();
        var sink = MakeSink(spy);
        await sink.EnqueueAsync(new BufferedCompletion(MakeRequest(claimed, workerId), TestNamespace, enq.JobId, 0));
        sink.CompleteWriter();
        await sink.RunFlusherAsync();

        // Primary: job reaches Done via batch.
        Assert.Equal(JobStatusCode.Done, (await ReadJobAsync(enq.JobId, ct)).Status);

        // Secondary: JobCompletion/JobFinished wake fired (batch path), NOT the parent/key wakes.
        Assert.Contains(
            spy.Wakes,
            w => w.Channel.Kind == WorkerWakeupChannelKind.JobCompletion && w.Reason == WorkerWakeupReason.JobFinished
        );
        Assert.DoesNotContain(
            spy.Wakes,
            w => w.Channel.Kind == WorkerWakeupChannelKind.AllWorkerNamespaces && w.Reason == WorkerWakeupReason.WorkAvailable
        );
        Assert.DoesNotContain(
            spy.Wakes,
            w =>
                w.Channel.Kind == WorkerWakeupChannelKind.WorkerNamespace
                && w.Channel.Name == "ns:" + TestNamespace
                && w.Reason == WorkerWakeupReason.WorkAvailable
        );
    }

    [Fact(
        DisplayName = "One failed per-job completion leaves only that job for recovery: later jobs in the batch still complete and already-committed jobs still get their wake"
    )]
    public async Task Failed_fallback_strands_only_its_own_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect, leaseTtl, ns, workerId) = await DepsAsync(ct);

        if (dialect.Provider == DbProvider.Sqlite)
        {
            Assert.Skip("CompletionSink calls CompleteExecutionsBatch which is not supported on SQLite.");
        }

        // Two children (parent → batch self-filters → scalar fallback) and one plain row the batch
        // finalizes. The plain row goes last so it sits after the failing fallback in flush order.
        var firstChild = await StartedChildAsync(leaseTtl, ns, workerId, ct);
        var secondChild = await StartedChildAsync(leaseTtl, ns, workerId, ct);
        var plain = await StartedPlainAsync(leaseTtl, ns, workerId, ct);

        // Fail the first scalar fallback only. The set call is untouched, so it commits the plain row.
        var plan = new StoreFaultPlan();
        plan.ThrowBeforeCompleteOnce();
        var spy = new WakeupSpy();
        var sink = new CompletionSink(
            new FaultInjectingExecutionStore(Services.GetRequiredService<IExecutionStore>(), plan),
            new WorkerWakeupPublisher(spy),
            Options.Create(new JobsOptions { BatchCompletionSize = 100 })
        );

        await sink.EnqueueAsync(new BufferedCompletion(MakeRequest(firstChild.Claimed, workerId), TestNamespace, firstChild.JobId, 0));
        await sink.EnqueueAsync(new BufferedCompletion(MakeRequest(secondChild.Claimed, workerId), TestNamespace, secondChild.JobId, 0));
        await sink.EnqueueAsync(new BufferedCompletion(MakeRequest(plain.Claimed, workerId), TestNamespace, plain.JobId, 0));
        sink.CompleteWriter();
        await sink.RunFlusherAsync();

        // The injected failure strands its own job and nothing else.
        Assert.Equal(JobStatusCode.Executing, (await ReadJobAsync(firstChild.JobId, ct)).Status);

        // The fallback after it still ran: iteration does not stop at the first failure.
        Assert.Equal(JobStatusCode.Done, (await ReadJobAsync(secondChild.JobId, ct)).Status);

        // The set call committed the plain row before the failure, so it is terminal, not rolled back.
        Assert.Equal(JobStatusCode.Done, (await ReadJobAsync(plain.JobId, ct)).Status);

        // And it still got its deferred wake, which the old single-catch flush skipped.
        Assert.Contains(
            spy.Wakes,
            w => w.Channel.Kind == WorkerWakeupChannelKind.JobCompletion && w.Reason == WorkerWakeupReason.JobFinished
        );
    }

    // ---------- helpers ----------

    // A child of a Suspended parent, claimed and started: the batch routine self-filters it (it has a
    // parent), so it reaches the scalar fallback.
    private async Task<(long JobId, ClaimedJob Claimed)> StartedChildAsync(int leaseTtl, short ns, int workerId, CancellationToken ct)
    {
        var parentEnq = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-parent-one", JobPayload.None), ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(parentEnq, ct));
        var child = Assert.Single(await ReadChildrenAsync(parentEnq.JobId, ct));
        return (child.Id, await ClaimAndStartAsync(child.Id, leaseTtl, ns, workerId, ct));
    }

    // A plain row: no parent, no exclusive key, so the set-based routine finalizes it.
    private async Task<(long JobId, ClaimedJob Claimed)> StartedPlainAsync(int leaseTtl, short ns, int workerId, CancellationToken ct)
    {
        var enq = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(3, 4))), ct);
        return (enq.JobId, await ClaimAndStartAsync(enq.JobId, leaseTtl, ns, workerId, ct));
    }

    private async Task<ClaimedJob> ClaimAndStartAsync(long jobId, int leaseTtl, short ns, int workerId, CancellationToken ct)
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

    private async Task<(IDbSession Db, ISqlDialect Dialect, int LeaseTtl, short Ns, int WorkerId)> DepsAsync(CancellationToken ct)
    {
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var dialect = Services.GetRequiredService<ISqlDialect>();
        var leaseTtl = Services.GetRequiredService<IOptions<JobsOptions>>().Value.LeaseTtlSeconds;
        var worker = await Db.From<JobWorker>().Where(w => w.NamespaceId == ns).SingleOrDefaultAsync(ct);
        Assert.NotNull(worker);
        return (Db, dialect, leaseTtl, ns, worker!.Id);
    }

    private CompletionSink MakeSink(WakeupSpy spy) =>
        new(
            Services.GetRequiredService<IExecutionStore>(),
            new WorkerWakeupPublisher(spy),
            Options.Create(new JobsOptions { BatchCompletionSize = 100 })
        );

    private static CompleteExecutionRequest MakeRequest(ClaimedJob claimed, int workerId) =>
        new(
            JobId: claimed.JobId,
            WorkerId: workerId,
            ExpectedExecutionNumber: claimed.ExecutionNumber,
            Outcome: ExecutionOutcome.Succeeded,
            ResultFormatId: 0,
            Result: ReadOnlyMemory<byte>.Empty
        );

    private async Task<IReadOnlyList<Job>> ReadChildrenAsync(long parentId, CancellationToken ct)
    {
        return await Db.From<Job>().Where(j => j.ParentId == parentId).ToListAsync(ct);
    }

    private sealed class WakeupSpy : IWorkerWakeup
    {
        private readonly ConcurrentBag<(WorkerWakeupChannel Channel, WorkerWakeupReason Reason)> _wakes = new();

        public IReadOnlyCollection<(WorkerWakeupChannel Channel, WorkerWakeupReason Reason)> Wakes => _wakes;

        public ValueTask WakeAsync(WorkerWakeupChannel channel, WorkerWakeupReason reason, CancellationToken ct = default)
        {
            _wakes.Add((channel, reason));
            return ValueTask.CompletedTask;
        }

        public ValueTask<WorkerWakeupWaitResult> WaitAsync(WorkerWakeupChannel channel, TimeSpan timeout, CancellationToken ct) =>
            ValueTask.FromResult(WorkerWakeupWaitResult.TimedOut);
    }
}
