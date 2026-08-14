using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Conformance for the handler-initiated control verbs (<c>ctx.FailAsync</c> / <c>CancelAsync</c> /
/// <c>PauseAsync</c>): each is a deliberate decision that finalizes the attempt through
/// <c>complete_execution</c> to a terminal/hold Status, does not return to user code, leaves the
/// failure budget untouched, writes no result, and stamps the matching reason + lifecycle event.
/// </summary>
[ConformanceSpec(
    "handler-control.terminal-decisions",
    "Handler Fail Cancel Pause finalize the attempt without returning to user code",
    Area = "Control",
    Contract = "Handler control verbs and non-retryable exceptions finalize the attempt without returning to user code, budget untouched and no result written.",
    Arrange = "Handlers that call ctx.FailAsync, CancelAsync, or PauseAsync, or throw a non-retryable exception, are registered.",
    Act = "Each job runs once, then held jobs are resumed via external control.",
    Assert = "Each attempt finalizes through complete_execution to its terminal or hold status without returning to user code, budget untouched, no result written."
)]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.CompleteExecutionAsync))]
public abstract class HandlerControlSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(
        DisplayName = "Handler fail lands terminal Failed with budget untouched, no result, the matching reason, and no post-control user code"
    )]
    public async Task Fail_ends_terminal_failed_without_returning_to_user_code()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-handler-fail", JobPayload.None), ct);

        Assert.Equal(RunOnceOutcome.Failed, await Runtime.RunOnceAsync(enqueued, ct));

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Failed, job.Status);
        Assert.Null(job.LeasedByWorkerId);
        Assert.Null(job.NextRunAtUtc);
        Assert.Equal(0, job.FailureCount);
        var finishedEvent = await ReadLatestEventAsync(enqueued.JobId, EventCode.JobExecutionFinished, ct);
        Assert.Equal(JobEventReasonCode.JobHandlerFailed, finishedEvent.ReasonCode);

        // Did not return to user code, and the failed attempt persisted no result payload.
        Assert.Equal(1, await CountVariableAsync(enqueued.JobId, "ran.before", ct));
        Assert.Equal(0, await CountVariableAsync(enqueued.JobId, "ran.after", ct));
        Assert.Null(await Services.GetRequiredService<IJobStore>().GetJobResultAsync(enqueued.JobId, null, ct));

        // A handler fail is a failure: the finished event carries ExecutionStatusCode.Failed and there is
        // no separate cancel/pause lifecycle event.
        Assert.Equal(1, await CountFinishedWithStatusAsync(enqueued.JobId, ExecutionStatusCode.Failed, ct));
        Assert.Equal(0, await CountEventsAsync(enqueued.JobId, EventCode.JobCancelled, ct));
        Assert.Equal(0, await CountEventsAsync(enqueued.JobId, EventCode.JobPaused, ct));
    }

    [Fact(DisplayName = "A non-retryable exception lands terminal Failed without retries")]
    public async Task Non_retryable_exception_lands_terminal_failed_without_retries()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-throw-not-implemented", JobPayload.None), ct);

        Assert.Equal(RunOnceOutcome.Failed, await Runtime.RunOnceAsync(enqueued, ct));

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Failed, job.Status);
        Assert.Null(job.NextRunAtUtc);
        Assert.Equal(0, job.FailureCount);
        var finishedEvent = await ReadLatestEventAsync(enqueued.JobId, EventCode.JobExecutionFinished, ct);
        Assert.Equal(JobEventReasonCode.JobNonRetryableException, finishedEvent.ReasonCode);
        Assert.NotNull(job.RetentionUntilUtc);

        // A single failed finished event: the attempt was never re-armed for a retry.
        Assert.Equal(1, await CountVariableAsync(enqueued.JobId, "ran.before", ct));
        Assert.Equal(1, await CountFinishedWithStatusAsync(enqueued.JobId, ExecutionStatusCode.Failed, ct));

        // A second tick on this job finds nothing: the job is terminal, not waiting on a retry.
        // Target the job id: a namespace-wide tick can legitimately claim a manifest schedule slot
        // whenever the test happens to straddle the slot's cron boundary.
        Assert.Equal(RunOnceOutcome.NothingClaimed, await Runtime.RunOnceAsync(TestNamespace, enqueued.JobId, ct));
    }

    [Fact(DisplayName = "Handler cancel lands terminal Cancelled with the matching reason, no result, and a JobCancelled lifecycle event")]
    public async Task Cancel_ends_terminal_cancelled_and_emits_job_cancelled()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-handler-cancel", JobPayload.None), ct);

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Cancelled, job.Status);
        Assert.Null(job.LeasedByWorkerId);
        Assert.Null(job.NextRunAtUtc);
        Assert.Equal(0, job.FailureCount);
        var cancelEvent = await ReadLatestEventAsync(enqueued.JobId, EventCode.JobCancelled, ct);
        Assert.Equal(JobEventReasonCode.JobHandlerCancelled, cancelEvent.ReasonCode);

        Assert.Equal(0, await CountVariableAsync(enqueued.JobId, "ran.after", ct));
        Assert.Null(await Services.GetRequiredService<IJobStore>().GetJobResultAsync(enqueued.JobId, null, ct));

        Assert.Equal(1, await CountFinishedWithStatusAsync(enqueued.JobId, ExecutionStatusCode.Cancelled, ct));
        Assert.Equal(1, await CountEventsAsync(enqueued.JobId, EventCode.JobCancelled, ct));
    }

    [Fact(DisplayName = "Handler pause holds Paused with no next run, the matching reason, no result, and a JobPaused lifecycle event")]
    public async Task Pause_holds_paused_with_no_next_run_and_emits_job_paused()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-handler-pause", JobPayload.None), ct);

        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Paused, job.Status);
        Assert.Null(job.LeasedByWorkerId);
        Assert.Null(job.NextRunAtUtc);
        Assert.Equal(0, job.FailureCount);
        var pauseEvent = await ReadLatestEventAsync(enqueued.JobId, EventCode.JobPaused, ct);
        Assert.Equal(JobEventReasonCode.JobHandlerPaused, pauseEvent.ReasonCode);

        Assert.Equal(0, await CountVariableAsync(enqueued.JobId, "ran.after", ct));
        Assert.Null(await Services.GetRequiredService<IJobStore>().GetJobResultAsync(enqueued.JobId, null, ct));

        Assert.Equal(1, await CountFinishedWithStatusAsync(enqueued.JobId, ExecutionStatusCode.Paused, ct));
        Assert.Equal(1, await CountEventsAsync(enqueued.JobId, EventCode.JobPaused, ct));

        // A paused Job is not claimable: a second tick on its id finds nothing and leaves it Paused.
        // Target the job id: a namespace-wide tick can legitimately claim a manifest schedule slot
        // whenever the test happens to straddle the slot's cron boundary.
        Assert.Equal(RunOnceOutcome.NothingClaimed, await Runtime.RunOnceAsync(TestNamespace, enqueued.JobId, ct));
        Assert.Equal(JobStatusCode.Paused, (await ReadJobAsync(enqueued.JobId, ct)).Status);
    }

    [Fact(DisplayName = "A handler-paused job resumes to Ready via external control")]
    public async Task Paused_job_resumes_to_ready_via_external_control()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-handler-pause", JobPayload.None), ct);

        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(JobStatusCode.Paused, (await ReadJobAsync(enqueued.JobId, ct)).Status);

        var resumed = await Jobs.ResumeAsync(JobLookup.ById(enqueued.JobId), "operator resumed", ct: ct);
        Assert.Equal(JobControlAction.Applied, resumed.Action);
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(enqueued.JobId, ct)).Status);
    }
}
