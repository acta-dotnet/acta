using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Worker;

/// <summary>
/// Conformance for whole-job deadlines. A Strict deadline auto-terminates an overdue job at
/// admission and refuses to re-arm a retry whose next attempt would land past the deadline, without
/// consuming the retry budget. An Advisory deadline never auto-terminates; the handler observes
/// ctx.IsOverdue and decides.
/// </summary>
[ConformanceSpec(
    "deadline.strict",
    "A Strict deadline terminates an overdue job and blocks a retry past the deadline",
    Area = "Execution",
    Contract = "A Strict deadline lands the job Cancelled with JobDeadlineExceeded at admission or when the next retry would overshoot, without consuming the retry budget."
)]
public sealed class DeadlineSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    [Fact(DisplayName = "Strict admission cancels without running the handler")]
    public async Task Strict_overdue_at_admission_cancels_without_running_handler()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "deadline-strict-probe", JobPayload.None), ct);

        await MakeOverdueAsync(enqueued.JobId, ct);
        await Runtime.RunOnceAsync(enqueued, ct).WaitAsync(SpecWaits.Gate, ct);

        var snapshot = await Jobs.GetAsync(enqueued, ct);
        Assert.Equal(JobStatusCode.Cancelled, snapshot!.Status);
        Assert.Equal(0, snapshot.FailureCount);

        var events = await GetEventsByJobId.Run(Services, enqueued.JobId, ct);
        var yielded = events.Where(e => e.EventCode == EventCode.JobExecutionYielded).OrderByDescending(e => e.Id).First();
        Assert.Equal("cancelled", yielded.Outcome);
        Assert.Equal(EventReason.DeadlineExceeded, yielded.Reason);

        // A read checks no fence, so any execution of the job reads its slot.
        var ran = await CheckpointSlot.GetAsync(
            Services.GetRequiredService<ExecutionOperations>(),
            new ExecutionFence(enqueued.JobId, 0, 0),
            CheckpointKindCode.Variable,
            "ran",
            ct
        );
        Assert.Null(ran);
    }

    [Fact(DisplayName = "Strict blocks a retry past the deadline")]
    public async Task Strict_retry_past_deadline_is_terminal_not_rearmed()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "deadline-retry-probe", JobPayload.None), ct);

        await Runtime.RunOnceAsync(enqueued, ct).WaitAsync(SpecWaits.Gate, ct);

        var snapshot = await Jobs.GetAsync(enqueued, ct);
        Assert.Equal(JobStatusCode.Cancelled, snapshot!.Status);
        Assert.Equal(0, snapshot.FailureCount);

        var events = await GetEventsByJobId.Run(Services, enqueued.JobId, ct);
        var yielded = events.Where(e => e.EventCode == EventCode.JobExecutionYielded).OrderByDescending(e => e.Id).First();
        Assert.Equal("cancelled", yielded.Outcome);
        Assert.Equal(EventReason.DeadlineExceeded, yielded.Reason);
    }

    [Fact(DisplayName = "Advisory never auto-terminates")]
    public async Task Advisory_runs_handler_even_when_overdue()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "deadline-advisory-probe", JobPayload.None), ct);

        await MakeOverdueAsync(enqueued.JobId, ct);
        await Runtime.RunOnceAsync(enqueued, ct).WaitAsync(SpecWaits.Gate, ct);

        var snapshot = await Jobs.GetAsync(enqueued, ct);
        Assert.Equal(JobStatusCode.Succeeded, snapshot!.Status);
        Assert.Equal(0, snapshot.FailureCount);

        // A read checks no fence, so any execution of the job reads its slot.
        var overdue = await CheckpointSlot.GetAsync(
            Services.GetRequiredService<ExecutionOperations>(),
            new ExecutionFence(enqueued.JobId, 0, 0),
            CheckpointKindCode.Variable,
            "overdue",
            ct
        );
        Assert.NotNull(overdue);

        var events = await GetEventsByJobId.Run(Services, enqueued.JobId, ct);
        Assert.DoesNotContain(events, e => e.Reason == EventReason.DeadlineExceeded);
    }

    /// <summary>
    /// Puts the job's deadline instant in the past. A whole-job deadline is
    /// <c>jobs.created_at_utc + deadline_seconds</c>, so moving the anchor back is the same state a
    /// probe with a one-second deadline reaches by being left alone, without spending the second: the
    /// subject here is what admission decides about an overdue job, never how the job got that way.
    /// </summary>
    private async Task MakeOverdueAsync(long jobId, CancellationToken ct)
    {
        // The anchor comes off the DATABASE clock, the one admission compares the derived deadline
        // against. Back-dating from this process's clock would fold container clock skew into the
        // stamp, and the amount of back-dating is not what makes this job overdue - the clock it is
        // measured from is.
        var anchor = (await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct)).AddMinutes(-5);
        var affected = await Db.From<JobEntity>()
            .Where(j => j.Id == jobId)
            .UpdateOnlyAsync(() => new JobEntity { CreatedAtUtc = anchor }, ct);
        Assert.Equal(1, affected);
    }
}
