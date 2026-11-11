using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Conformance for durable steps: <c>ctx.RunStepAsync</c> runs a body once, persists the
/// outcome to <c>steps</c>, and replay-skips a succeeded step. A body that throws fails the attempt
/// and spends the job's budget; a wait inside a body resumes without spending any; an at-most-once
/// body that throws ends its step failed, with <c>StepFailedException</c>.
/// </summary>
[ConformanceSpec(
    "steps.run-and-retry",
    "RunStepAsync runs once, replays results, and runs again on the job's retry",
    Area = "Steps",
    Contract = "A step runs once and replay-skips a success, a throwing body spends the job's budget, a wait in a body charges nothing, and an at-most-once failure is final."
)]
public sealed class StepSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    [Fact(DisplayName = "A typed step runs its body once and returns the stored result")]
    public async Task Typed_step_runs_once_and_returns_result()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-step-basic", JobPayload.None), ct);

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(enqueued.JobId, ct)).Status);
        Assert.Equal("ok", await Jobs.GetResultAsync<string>(enqueued, ct));
        Assert.Equal(1, JobStepProbes.BodyInvocations[enqueued.JobId]);

        var step = Assert.Single(await ReadStepsAsync(enqueued.JobId, ct));
        Assert.Equal("compute", step.StepName);
        Assert.Equal(StepStatusCode.Succeeded, step.Status);
        Assert.NotEqual(0, step.ResultFormatId);
        Assert.NotNull(step.Result);
    }

    [Fact(DisplayName = "A void step succeeds with no result payload")]
    public async Task Void_step_succeeds_with_no_result_payload()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-step-void", JobPayload.None), ct);

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        var step = Assert.Single(await ReadStepsAsync(enqueued.JobId, ct));
        Assert.Equal("side-effect", step.StepName);
        Assert.Equal(StepStatusCode.Succeeded, step.Status);
        Assert.Equal(0, step.ResultFormatId);
        Assert.Null(step.Result);
        Assert.Equal(1, await CountVariableAsync(enqueued.JobId, "step.ran", ct));
    }

    [Fact(DisplayName = "A succeeded step replays its stored result without re-running the body")]
    public async Task Succeeded_step_replays_without_re_running_the_body()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-step-replay", JobPayload.None), ct);

        // Tick 1: step runs, then the handler waits on a signal.
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(1, JobStepProbes.BodyInvocations[enqueued.JobId]);
        Assert.Equal(StepStatusCode.Succeeded, (await ReadStepsAsync(enqueued.JobId, ct)).Single().Status);

        // Tick 2: raise the signal and replay - the step must replay-skip (no second invocation).
        Assert.Equal(ControlAction.Applied, (await Jobs.RaiseSignalAsync(enqueued, "proceed", JobPayload.None, ct: ct)).Action);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        Assert.Equal(1, JobStepProbes.BodyInvocations[enqueued.JobId]);
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(enqueued.JobId, ct)).Status);
        Assert.Equal(1, await CountVariableAsync(enqueued.JobId, "step.value", ct));
    }

    [Fact(DisplayName = "A wait inside an ordinary step resumes and completes, and the job's failure count stays untouched")]
    public async Task Wait_inside_an_ordinary_step_resumes_and_completes()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-step-wait", JobPayload.None), ct);

        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(StepStatusCode.Pending, (await ReadStepsAsync(enqueued.JobId, ct)).Single().Status);

        Assert.Equal(ControlAction.Applied, (await Jobs.RaiseSignalAsync(enqueued, "approval", JobPayload.None, ct: ct)).Action);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        // The replay ran the body again up to the wait, which returned because the signal is set.
        Assert.Equal(2, JobStepProbes.BodyInvocations[enqueued.JobId]);
        Assert.Equal("approved", await Jobs.GetResultAsync<string>(enqueued, ct));
        Assert.Equal(StepStatusCode.Succeeded, (await ReadStepsAsync(enqueued.JobId, ct)).Single().Status);
        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Succeeded, job.Status);
        Assert.Equal(0, job.FailureCount);
    }

    [Fact(DisplayName = "A wait inside an ordinary step charges nothing, so a later failure keeps the job's whole budget")]
    public async Task Wait_inside_an_ordinary_step_charges_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-step-wait-then-fail", JobPayload.None), ct);

        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(ControlAction.Applied, (await Jobs.RaiseSignalAsync(enqueued, "approval", JobPayload.None, ct: ct)).Action);

        // The resume after the wait spent nothing, so the failure past it is the first of the job's two attempts.
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(1, (await ReadJobAsync(enqueued.JobId, ct)).FailureCount);

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(3, JobStepProbes.BodyInvocations[enqueued.JobId]);
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(enqueued.JobId, ct)).Status);
    }

    [Fact(
        DisplayName = "A throwing ordinary step fails the attempt, spends the job's budget, and the retry replays the steps that succeeded"
    )]
    public async Task Throwing_ordinary_step_spends_the_jobs_budget()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-step-throw", JobPayload.None), ct);

        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(1, (await ReadJobAsync(enqueued.JobId, ct)).FailureCount);
        var failed = await ReadLatestEventAsync(enqueued.JobId, EventCode.JobExecutionFailed, ct);
        Assert.Equal(EventReason.UnhandledException, EventDetail(failed, "reason"));

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(1, JobStepProbes.StepBodyInvocations[(enqueued.JobId, "reserve")]);
        Assert.Equal(2, JobStepProbes.StepBodyInvocations[(enqueued.JobId, "charge")]);
        Assert.All(await ReadStepsAsync(enqueued.JobId, ct), step => Assert.Equal(StepStatusCode.Succeeded, step.Status));
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(enqueued.JobId, ct)).Status);
    }

    [Fact(DisplayName = "A failed at-most-once step throws StepFailedException and fails the parent at once when uncaught")]
    public async Task Failed_step_fails_the_parent_when_uncaught()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-step-fail", JobPayload.None), ct);

        // The body throws once: the step is terminal, and the parent's remaining budget is not spent on it.
        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(enqueued, ct));

        var step = Assert.Single(await ReadStepsAsync(enqueued.JobId, ct));
        Assert.Equal(StepStatusCode.Failed, step.Status);
        Assert.Equal("boom", step.FailureMessage);

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Failed, job.Status);
        var failedEvent = await ReadLatestEventAsync(enqueued.JobId, EventCode.JobFailed, ct);
        // Uncaught, the failure is non-retryable: every retry would replay the failed step and throw again.
        Assert.Equal(EventReason.NonRetryableException, EventDetail(failedEvent, "reason"));
        Assert.Equal(1, JobStepProbes.BodyInvocations[enqueued.JobId]);
    }

    [Fact(DisplayName = "A state reset inside a step's body aborts the attempt, and the failure names the step")]
    public async Task A_reset_inside_a_step_body_aborts_the_attempt()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-step-reset-inside", JobPayload.None), ct);

        // The reset deleted the step's row, so its completion wrote nothing: the attempt is charged, never reported done.
        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(enqueued, ct));

        Assert.Equal(JobStatusCode.Failed, (await ReadJobAsync(enqueued.JobId, ct)).Status);
        Assert.Empty(await ReadStepsAsync(enqueued.JobId, ct));
        var failed = await ReadLatestEventAsync(enqueued.JobId, EventCode.JobFailed, ct);
        Assert.Equal(EventReason.AttemptAborted, EventDetail(failed, "reason"));
        Assert.Contains("'resets'", failed.EventMessage, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "A step started twice at one version completes once, and the later completion reports StaleVersion")]
    public async Task CompleteJobStep_returns_stale_version_when_the_step_was_started_twice()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Services.GetRequiredService<ExecutionOperations>();

        // A real job row to own the step (enqueue only, staged Executing as a claim leaves it; never run).
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-step-basic", JobPayload.None), ct);
        var job = await ExecutionFenceStaging.ExecutingAsync(Db, enqueued.JobId, ct);

        // Two starts of one pending step both invoke it at the version the row holds, which a replay leaves unchanged.
        var first = await store.StartJobStepAsync(job, "compute", atMostOnce: false, ct);
        var second = await store.StartJobStepAsync(job, "compute", atMostOnce: false, ct);
        Assert.Equal(StartJobStepOutcomeCode.Invoke, first.Outcome);
        Assert.Equal(StartJobStepOutcomeCode.Invoke, second.Outcome);
        Assert.Equal(first.Version, second.Version);
        Assert.Equal(CompleteJobStepOutcomeCode.Succeeded, (await store.CompleteJobStepAsync(Success(job, first.Version), ct)).Outcome);

        // The later completion carries the version the step has moved past: it loses the CAS, not a phantom success.
        var stale = await store.CompleteJobStepAsync(Success(job, second.Version), ct);

        Assert.Equal(CompleteJobStepOutcomeCode.StaleVersion, stale.Outcome);
        var step = Assert.Single(await ReadStepsAsync(enqueued.JobId, ct));
        Assert.Equal(StepStatusCode.Succeeded, step.Status);
        Assert.Equal(first.Version + 1, step.Version);
    }

    [Fact(DisplayName = "CompleteJobStep reports StaleVersion rather than throwing when the step row is absent")]
    public async Task CompleteJobStep_returns_stale_version_when_the_step_row_is_absent()
    {
        var ct = TestContext.Current.CancellationToken;

        // A real job row owns no step (StartJobStep never ran, or the step was reset away).
        // A completion arriving for an absent step must report StaleVersion, not throw - the routine
        // always yields one decision row regardless of whether a matching step exists.
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-step-basic", JobPayload.None), ct);
        var job = await ExecutionFenceStaging.ExecutingAsync(Db, enqueued.JobId, ct);

        var outcome = await Services.GetRequiredService<ExecutionOperations>().CompleteJobStepAsync(Success(job, 1), ct);

        Assert.Equal(CompleteJobStepOutcomeCode.StaleVersion, outcome.Outcome);
        Assert.Empty(await ReadStepsAsync(enqueued.JobId, ct));
    }

    private static CompleteJobStepCommand Success(ExecutionFence job, int expectedVersion) =>
        new(job, "compute", Succeeded: true, ResultFormatId: 0, Result: null, FailureMessage: null, ExpectedVersion: expectedVersion);

    private async Task<IReadOnlyList<StepEntity>> ReadStepsAsync(long jobId, CancellationToken ct)
    {
        return await Db.From<StepEntity>().Where(a => a.JobId == jobId).ToListAsync(ct);
    }
}
