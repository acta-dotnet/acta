using Acta.Features.Jobs;
using Acta.Payloads;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Jobs;

/// <summary>
/// Operator RescheduleAsync: moves a waiting job's next-run instant, re-arming Paused/Suspended rows
/// Ready so the new instant actually fires; Dispatched/Executing/terminal rows are rejected.
/// </summary>
[ConformanceSpec(
    "job.reschedule",
    "Operator reschedule moves a job's cursor, rejecting in-flight or terminal jobs.",
    Area = "Control",
    Contract = "RescheduleAsync moves Paused, Suspended, or Ready rows to the requested instant, re-arms Paused or Suspended Ready, and rejects in-flight or terminal rows.",
    Arrange = "A Ready job due far in the future, one job mid-execution, one completed job, and no job for an unknown lookup.",
    Act = "RescheduleAsync is invoked against each job.",
    Assert = "The Ready job reaches the requested instant with an audited event, in-flight and terminal jobs are rejected unchanged, and the unknown lookup is NotFound."
)]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.RescheduleJobAsync))]
public abstract class JobRescheduleSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "RescheduleAsync moves a Ready job's next run to the requested instant and bumps the runtime version")]
    public async Task Reschedule_ready_job_moves_cursor()
    {
        var ct = TestContext.Current.CancellationToken;
        var far = DateTime.UtcNow.AddDays(30);
        var near = DateTime.UtcNow.AddMinutes(5);

        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3)), NextRunAtUtc: far),
            ct
        );
        var before = await ReadJobAsync(enqueued.JobId, ct);

        var result = await Jobs.RescheduleAsync(enqueued, near, "ops", "spec-actor", ct);

        Assert.Equal(JobControlAction.Applied, result.Action);
        Assert.Equal(JobStatusCode.Ready, result.Status);

        var snapshot = await Jobs.GetAsync(enqueued, ct);
        Assert.NotNull(snapshot);
        Assert.Equal(JobStatusCode.Ready, snapshot!.Status);
        Assert.NotNull(snapshot.NextRunAtUtc);
        Assert.InRange(snapshot.NextRunAtUtc!.Value, near.AddSeconds(-1), near.AddSeconds(1));

        var after = await ReadJobAsync(enqueued.JobId, ct);
        Assert.True(after.Version > before.Version);

        var evt = await ReadSingleEventAsync(enqueued.JobId, JobEventCode.JobRescheduled, ct);
        Assert.Equal("spec-actor", evt.ActorKey);
    }

    [Fact(DisplayName = "RescheduleAsync rejects executing and terminal jobs without mutating them")]
    public async Task Reschedule_rejects_executing_and_terminal()
    {
        var ct = TestContext.Current.CancellationToken;

        var executingJob = await EnqueueAsync(ct);
        await SetJobStatusAsync(Db, executingJob, (byte)JobStatusCode.Executing, ct);
        var beforeExecuting = await ReadJobAsync(executingJob, ct);

        var completedJob = (await EnqueueAndRunAsync("add-numbers", new AddNumbers(2, 3), ct)).JobId;
        var beforeCompleted = await ReadJobAsync(completedJob, ct);

        var next = DateTime.UtcNow.AddHours(1);
        var resultExecuting = await Jobs.RescheduleAsync(JobLookup.ById(executingJob), next, ct: ct);
        var resultCompleted = await Jobs.RescheduleAsync(JobLookup.ById(completedJob), next, ct: ct);

        Assert.Equal(JobControlAction.Rejected, resultExecuting.Action);
        Assert.Equal(JobStatusCode.Executing, resultExecuting.Status);
        Assert.Equal(JobControlAction.Rejected, resultCompleted.Action);
        Assert.Equal(JobStatusCode.Done, resultCompleted.Status);

        var afterExecuting = await ReadJobAsync(executingJob, ct);
        Assert.Equal(beforeExecuting.Version, afterExecuting.Version);
        Assert.Equal(beforeExecuting.NextRunAtUtc, afterExecuting.NextRunAtUtc);

        var afterCompleted = await ReadJobAsync(completedJob, ct);
        Assert.Equal(beforeCompleted.Version, afterCompleted.Version);
        Assert.Equal(beforeCompleted.NextRunAtUtc, afterCompleted.NextRunAtUtc);
    }

    [Fact(DisplayName = "RescheduleAsync returns NotFound for an unknown lookup")]
    public async Task Reschedule_unknown_is_notfound()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await Jobs.RescheduleAsync(JobLookup.ById(999_999_999_999L), DateTime.UtcNow.AddMinutes(5), ct: ct);
        Assert.Equal(JobControlAction.NotFound, result.Action);
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
