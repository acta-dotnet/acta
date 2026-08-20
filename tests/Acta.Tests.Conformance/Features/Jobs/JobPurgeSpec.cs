using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Jobs;

/// <summary>
/// Operator PurgeAsync: hard-deletes a terminal job. Only Succeeded/Failed/Cancelled rows qualify; the
/// routine deletes the job's events and alerts explicitly (both FK-less) then the job row itself
/// (CASCADE sweeps runtimes/schedules/steps/results/checkpoints/tags). Always emits job.purged (not
/// audit-gated), and rejects a terminal job that has child jobs (parent_id carries no DB
/// cascade, so deleting the parent would orphan the child's lineage).
/// </summary>
[ConformanceSpec(
    "job.purge",
    "Operator purge hard-deletes a terminal job.",
    Area = "Control",
    Contract = "PurgeAsync deletes a terminal job's events, alerts, and row (cascade sweeps the rest), always emits job.purged, and rejects non-terminal or live-child jobs.",
    Arrange = "A Succeeded job with its own events and an alert, an Executing job, a Succeeded parent with child jobs, no job for an unknown lookup.",
    Act = "PurgeAsync is invoked against each job.",
    Assert = "The Succeeded job is Applied with its row, events, and alerts gone plus a job.purged event, the others are Rejected, and the unknown lookup is NotFound."
)]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.PurgeJobAsync))]
public abstract class JobPurgeSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "PurgeAsync hard-deletes a Succeeded job and audits job.purged")]
    public async Task Purge_done_job_deletes_it_and_audits()
    {
        var ct = TestContext.Current.CancellationToken;

        var completed = await EnqueueAndRunAsync("add-numbers", new AddNumbers(2, 3), ct);
        var before = await ReadJobAsync(completed.JobId, ct);

        // The completed run already left job.execution-started/finished events for this job_id; seed
        // an alert too, so purge's own-row cleanup of both tables is provable, not just the row delete.
        await AlertTestOps.RaiseAsync(
            Services,
            TestNamespace,
            completed.JobId,
            AlertOriginCode.Manual,
            AlertSeverityCode.Info,
            AlertKindCode.Manual,
            "purge-spec alert",
            "purge-spec message",
            "default",
            AlertDeliveryStatusCode.Pending,
            null,
            ct
        );
        Assert.NotEmpty(await Db.From<JobEvent>().Where(e => e.JobId == completed.JobId).ToListAsync(ct));
        Assert.NotEmpty(await Db.From<JobAlert>().Where(a => a.JobId == completed.JobId).ToListAsync(ct));

        var result = await Jobs.PurgeAsync(completed, "spec-actor", ct);

        Assert.Equal(ControlAction.Applied, result.Action);
        Assert.Null(result.Status);

        Assert.Null(await Jobs.GetAsync(completed, ct));
        Assert.Empty(await Db.From<JobEvent>().Where(e => e.JobId == completed.JobId).ToListAsync(ct));
        Assert.Empty(await Db.From<JobAlert>().Where(a => a.JobId == completed.JobId).ToListAsync(ct));

        var evt = await Db.From<JobEvent>()
            .Where(e => e.DefinitionId == before.DefinitionId && e.EventCode == EventCode.JobPurged)
            .SingleOrDefaultAsync(ct);
        Assert.NotNull(evt);
        Assert.Null(evt!.JobId);
        Assert.Null(evt.JobRef);
        Assert.Equal(ActorCode.Operator, evt.ActorCode);
        Assert.Equal("spec-actor", evt.ActorKey);
        Assert.Contains(completed.JobRef.Value.ToString(), evt.ReasonMessage);
    }

    [Fact(DisplayName = "PurgeAsync rejects a non-terminal job and leaves it intact")]
    public async Task Purge_rejects_non_terminal_job()
    {
        var ct = TestContext.Current.CancellationToken;

        var executingJob = await EnqueueAsync(ct);
        await SetJobStatusAsync(Db, executingJob, (byte)JobStatusCode.Executing, ct);

        var result = await Jobs.PurgeAsync(JobLookup.ById(executingJob), ct: ct);

        Assert.Equal(ControlAction.Rejected, result.Action);
        Assert.Equal(JobStatusCode.Executing, result.Status);

        var after = await ReadJobAsync(executingJob, ct);
        Assert.Equal(JobStatusCode.Executing, after.Status);
    }

    [Fact(DisplayName = "PurgeAsync rejects a terminal parent that still has a live child")]
    public async Task Purge_rejects_terminal_parent_with_live_child()
    {
        var ct = TestContext.Current.CancellationToken;

        // The child must be enqueued while the parent is still non-terminal (enqueue itself rejects a
        // child under an already-terminal parent), then the parent is driven to Succeeded, leaving the child
        // behind as the live descendant the purge guard must see.
        var parentId = await EnqueueAsync(ct);
        await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 1)), ParentJobId: parentId),
            ct
        );
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(parentId, ct));

        var result = await Jobs.PurgeAsync(JobLookup.ById(parentId), ct: ct);

        Assert.Equal(ControlAction.Rejected, result.Action);
        Assert.Equal(JobStatusCode.Succeeded, result.Status);

        Assert.NotNull(await Jobs.GetAsync(JobLookup.ById(parentId), ct));
    }

    [Fact(DisplayName = "PurgeAsync returns NotFound for an unknown lookup")]
    public async Task Purge_unknown_is_notfound()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await Jobs.PurgeAsync(JobLookup.ById(999_999_999_999L), ct: ct);
        Assert.Equal(ControlAction.NotFound, result.Action);
    }

    // ---------- helpers ----------

    private async Task<long> EnqueueAsync(CancellationToken ct)
    {
        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );
        return enqueued.JobId;
    }

    private static Task SetJobStatusAsync(IDbSession db, long jobId, byte statusCode, CancellationToken ct) =>
        db.ExecuteRawAsync(
            "UPDATE {schema}.runtimes SET status_code = @p_status WHERE job_id = @p_id",
            ct,
            ("@p_status", statusCode),
            ("@p_id", jobId)
        );
}
