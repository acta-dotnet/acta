using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>
/// Operator PurgeAsync: hard-deletes a terminal job. Only Succeeded/Failed/Cancelled rows qualify; the
/// routine deletes the job's events, alerts, tags, checkpoints, steps, schedules, the job row, and its jobs_state
/// row, none of which carries a key to the job. Always emits job.purged (not
/// audit-gated), and rejects a terminal job that has child jobs (parent_job_id carries no DB
/// cascade, so deleting the parent would orphan the child's lineage).
/// </summary>
[ConformanceSpec(
    "job.purge",
    "Operator purge hard-deletes a terminal job.",
    Area = "Control",
    Contract = "PurgeAsync deletes a terminal job's events, alerts, and every row of the job, always emits job.purged, and rejects non-terminal or live-child jobs."
)]
public sealed class PurgeJobSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    [Fact(DisplayName = "PurgeAsync hard-deletes a Succeeded job and audits job.purged")]
    public async Task Purge_done_job_deletes_it_and_audits()
    {
        var ct = TestContext.Current.CancellationToken;

        var completed = await EnqueueAndRunAsync("add-numbers", new AddNumbers(2, 3), ct);
        var before = await ReadJobAsync(completed.JobId, ct);

        // The completed run already left a succeeded event for this job; seed an alert too, so
        // purge's own-row cleanup of both tables is provable, not just the row delete.
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
        Assert.NotEmpty(await JobEventsAsync(completed.JobId, ct));
        Assert.NotEmpty(await Db.From<AlertEntity>().Where(a => a.JobId == completed.JobId).ToListAsync(ct));
        // A step and a checkpoint: no key cascades them, so the delete has to name each table. They are written as a
        // handler writes them, under the execution fence of an attempt the job stands executing, then it is finished again.
        var execution = Services.GetRequiredService<ExecutionOperations>();
        var fence = await ExecutionFenceStaging.ExecutingAsync(Db, completed.JobId, ct);
        await execution.StartStepAsync(fence, "seed-step", atMostOnce: false, ct);
        await execution.AccessCheckpointAsync(
            new AccessCheckpointCommand(
                AccessCheckpointAction.Set,
                fence,
                CheckpointKindCode.Variable,
                "seed-variable",
                JobPayloadFormat.Json.Id,
                "1"u8.ToArray()
            ),
            ct
        );
        await JobStateStaging.SetStatusAsync(Db, completed.JobId, JobStatusCode.Succeeded, ct);

        var result = await Jobs.PurgeAsync(completed, "spec-actor", ct);

        Assert.Equal(ControlAction.Applied, result.Action);
        Assert.Null(result.Status);

        Assert.Null(await Jobs.GetAsync(completed, ct));
        Assert.Empty(await Db.From<JobStateEntity>().Where(r => r.Id == completed.JobId).ToListAsync(ct));
        Assert.Empty(await Db.From<StepEntity>().Where(s => s.JobId == completed.JobId).ToListAsync(ct));
        Assert.Empty(await Db.From<CheckpointEntity>().Where(c => c.JobId == completed.JobId).ToListAsync(ct));
        Assert.Empty(await Db.From<AlertEntity>().Where(a => a.JobId == completed.JobId).ToListAsync(ct));

        // Every event of the job is gone but the tombstone, which keeps the job as its subject and
        // snapshots what no surviving row can carry any more.
        var tombstone = Assert.Single(await JobEventsAsync(completed.JobId, ct));
        Assert.Equal(EventCode.JobPurged, tombstone.EventCode);
        Assert.Equal(before.DefinitionId, tombstone.DefinitionId);
        Assert.Equal(completed.JobRef.ToString(), EventDetail(tombstone, "jobRef"));
        Assert.Equal("spec-actor", EventDetail(tombstone, "actor"));
    }

    private Task<IReadOnlyList<EventEntity>> JobEventsAsync(long jobId, CancellationToken ct) =>
        Db.From<EventEntity>().Where(e => e.EntityCode == EntityCode.Job && e.EntityId == jobId).ToListAsync(ct);

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
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 1)), ParentJobId: parentId),
            ct
        );
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(parentId, ct));

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

    private async Task<long> EnqueueAsync(CancellationToken ct)
    {
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );
        return enqueued.JobId;
    }

    private static Task SetJobStatusAsync(IDbSession db, long jobId, byte statusCode, CancellationToken ct) =>
        JobStateStaging.SetStatusAsync(db, jobId, statusCode, ct);
}
