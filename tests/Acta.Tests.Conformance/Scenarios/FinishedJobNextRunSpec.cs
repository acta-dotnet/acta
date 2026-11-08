using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Conformance for a finished job's next run: a job in Succeeded, Failed, or Cancelled is not waiting on a time, so
/// every path that lands one writes <c>next_run_at_utc</c> NULL, whatever instant the job held before.
/// </summary>
[ConformanceSpec(
    "finished-job.next-run",
    "A finished job has no next run time",
    Area = "Execution",
    Contract = "Every landing in Succeeded, Failed, or Cancelled writes next_run_at_utc NULL: a completion, a cancel and its cascade, a retire, and a reclaim."
)]
public sealed class FinishedJobNextRunSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    [Fact(DisplayName = "A completion clears the next run: a success, a laned success, and a spent retry budget")]
    public async Task A_completion_clears_the_next_run()
    {
        var ct = TestContext.Current.CancellationToken;
        RetryProbe.Reset(TestNamespace);
        var plain = await Jobs.EnqueueAsync(AddNumbers(), ct);
        var laned = await Jobs.EnqueueAsync(AddNumbers() with { Lane = "finished" }, ct);
        var failing = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "retry-probe", JobPayload.None), ct);

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(plain, ct));
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(laned, ct));
        // retry-probe allows three attempts with no backoff, so the third run spends the budget.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Runtime.RunOnceAsync(failing, ct);
        }

        await AssertFinishedAsync(plain.JobId, JobStatusCode.Succeeded, ct);
        await AssertFinishedAsync(laned.JobId, JobStatusCode.Succeeded, ct);
        await AssertFinishedAsync(failing.JobId, JobStatusCode.Failed, ct);
    }

    [Fact(DisplayName = "A cancel clears the next run of a delayed job and of its delayed descendants")]
    public async Task A_cancel_clears_the_next_run_down_the_tree()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(AddNumbers() with { DelaySeconds = 3600 }, ct);
        var child = await Jobs.EnqueueAsync(AddNumbers() with { DelaySeconds = 3600, ParentJobId = parent.JobId }, ct);

        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(parent, "no longer needed", ct: ct)).Action);

        await AssertFinishedAsync(parent.JobId, JobStatusCode.Cancelled, ct);
        await AssertFinishedAsync(child.JobId, JobStatusCode.Cancelled, ct);
    }

    [Fact(DisplayName = "A retire clears the next run of the delayed jobs it cancels")]
    public async Task A_retire_clears_the_next_run()
    {
        var ct = TestContext.Current.CancellationToken;
        var delayed = await Jobs.EnqueueAsync(AddNumbers() with { DelaySeconds = 3600 }, ct);

        var definition = await Operations.Definitions.GetAsync(TestNamespace, "add-numbers", ct);
        Assert.NotNull(definition);
        var retired = await Operations.Definitions.RetireAsync(TestNamespace, "add-numbers", definition!.Version, "gone", "spec", ct);
        Assert.Equal(ControlAction.Applied, retired.Action);

        await AssertFinishedAsync(delayed.JobId, JobStatusCode.Cancelled, ct);
    }

    [Fact(DisplayName = "Crash recovery that spends the last attempt leaves the failed job no next run")]
    public async Task A_reclaim_that_fails_the_job_clears_the_next_run()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var worker = await Db.From<WorkerEntity>().Where(w => w.NamespaceId == ns).SingleOrDefaultAsync(ct);
        Assert.NotNull(worker);
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "retry-probe", JobPayload.None), ct);
        // Two failures already counted, so the lost third attempt is the last retry-probe allows.
        Assert.Equal(
            1,
            await Db.From<JobStateEntity>()
                .Where(r => r.Id == enqueued.JobId)
                .UpdateOnlyAsync(() => new JobStateEntity { FailureCount = 2 }, ct)
        );

        await ChaosSpecHelpers.StrandExecutingAsync(Services, ns, worker!.Id, enqueued.JobId, ct);
        Assert.Equal(1, (await RecoverySweep.ReclaimAtLeastOneAsync(Services, ns, ct)).Reclaimed);

        await AssertFinishedAsync(enqueued.JobId, JobStatusCode.Failed, ct);
    }

    private EnqueueJobRequest AddNumbers() => new(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 2)));

    private async Task AssertFinishedAsync(long jobId, JobStatusCode status, CancellationToken ct)
    {
        var row = await ReadJobAsync(jobId, ct);
        Assert.Equal(status, row.Status);
        Assert.Null(row.NextRunAtUtc);
    }
}
