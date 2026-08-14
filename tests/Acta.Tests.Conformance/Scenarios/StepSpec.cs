using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Conformance for durable steps: <c>ctx.RunStepAsync</c> runs a body once, persists the
/// outcome to <c>steps</c>, replay-skips a succeeded slot, retries an in-budget failure
/// budget-neutrally for the parent, and throws <c>StepExhaustedException</c> when the budget is
/// spent (failing the parent as a normal handler exception when uncaught).
/// </summary>
[ConformanceSpec(
    "steps.run-and-retry",
    "RunStepAsync runs once, replays results, and retries until exhausted",
    Area = "Steps",
    Contract = "A step runs its body once, replay-skips a succeeded slot, retries an in-budget failure budget-neutrally, and exhausts to StepExhaustedException.",
    Arrange = "Handlers wrap their work in ctx.RunStepAsync durable steps with per-fact budgets.",
    Act = "Steps succeed, replay after a suspend, fail then succeed, or exhaust, and CompleteStep CAS losses hit advanced or absent slots.",
    Assert = "A body runs once per outcome, a succeeded slot replay-skips, in-budget failures retry budget-neutrally, and exhaustion throws StepExhaustedException."
)]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.StartStepAsync))]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.CompleteStepAsync))]
public abstract class StepSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "A typed step runs its body once and returns the stored result")]
    public async Task Typed_step_runs_once_and_returns_result()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-step-basic", JobPayload.None), ct);

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(enqueued.JobId, ct)).Status);
        Assert.Equal("ok", await Jobs.GetResultAsync<string>(enqueued, ct));
        Assert.Equal(1, JobStepProbes.BodyInvocations[enqueued.JobId]);

        var step = Assert.Single(await ReadStepsAsync(enqueued.JobId, ct));
        Assert.Equal("compute", step.Name);
        Assert.Equal(JobStepStatusCode.Succeeded, step.Status);
        Assert.NotEqual(0, step.ResultFormatId);
        Assert.NotNull(step.Result);
        Assert.Null(step.NextRetryAtUtc);
    }

    [Fact(DisplayName = "A void step succeeds with no result payload")]
    public async Task Void_step_succeeds_with_no_result_payload()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-step-void", JobPayload.None), ct);

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        var step = Assert.Single(await ReadStepsAsync(enqueued.JobId, ct));
        Assert.Equal("side-effect", step.Name);
        Assert.Equal(JobStepStatusCode.Succeeded, step.Status);
        Assert.Equal(0, step.ResultFormatId);
        Assert.Null(step.Result);
        Assert.Equal(1, await CountVariableAsync(enqueued.JobId, "step.ran", ct));
    }

    [Fact(DisplayName = "A succeeded step replays its stored result without re-running the body")]
    public async Task Succeeded_step_replays_without_re_running_the_body()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-step-replay", JobPayload.None), ct);

        // Tick 1: step runs, then the handler waits on a signal.
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(1, JobStepProbes.BodyInvocations[enqueued.JobId]);
        Assert.Equal(JobStepStatusCode.Succeeded, (await ReadStepsAsync(enqueued.JobId, ct)).Single().Status);

        // Tick 2: raise the signal and replay - the step must replay-skip (no second invocation).
        Assert.Equal(JobControlAction.Applied, (await Jobs.RaiseSignalAsync(enqueued, "proceed", ct: ct)).Action);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        Assert.Equal(1, JobStepProbes.BodyInvocations[enqueued.JobId]);
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(enqueued.JobId, ct)).Status);
        Assert.Equal(1, await CountVariableAsync(enqueued.JobId, "step.value", ct));
    }

    [Fact(DisplayName = "An in-budget retry inserts Pending attempt 1, increments attempt_number, and is budget-neutral for the parent")]
    public async Task In_budget_retry_is_budget_neutral_for_the_parent()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-step-retry", JobPayload.None), ct);

        // Tick 1: first execution inserts Pending attempt 1, the body fails, a retry is scheduled.
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        var afterFirst = (await ReadStepsAsync(enqueued.JobId, ct)).Single();
        Assert.Equal(JobStepStatusCode.Pending, afterFirst.Status);
        Assert.Equal((short)1, afterFirst.AttemptNumber);
        Assert.NotNull(afterFirst.NextRetryAtUtc);

        var jobAfterFirst = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal((short)0, jobAfterFirst.FailureCount);
        var rescheduleEvent = await ReadLatestEventAsync(enqueued.JobId, EventCode.JobRescheduled, ct);
        Assert.Equal(JobEventReasonCode.JobStepRetryScheduled, rescheduleEvent.ReasonCode);

        // Tick 2: attempt_number increments before the second invocation, which also fails.
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal((short)2, (await ReadStepsAsync(enqueued.JobId, ct)).Single().AttemptNumber);

        // Tick 3: the third invocation succeeds; the parent completes with an untouched budget.
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(3, JobStepProbes.BodyInvocations[enqueued.JobId]);
        Assert.Equal(JobStepStatusCode.Succeeded, (await ReadStepsAsync(enqueued.JobId, ct)).Single().Status);

        var done = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Succeeded, done.Status);
        Assert.Equal((short)0, done.FailureCount);
        Assert.Equal(1, await CountVariableAsync(enqueued.JobId, "step.done", ct));
    }

    [Fact(DisplayName = "An exhausted step throws StepExhaustedException and fails the parent when uncaught")]
    public async Task Exhausted_step_fails_the_parent_when_uncaught()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-step-exhaust", JobPayload.None), ct);

        // Tick 1: attempt 1 fails in budget (step MaxAttempts = 2) -> budget-neutral re-arm.
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(JobStepStatusCode.Pending, (await ReadStepsAsync(enqueued.JobId, ct)).Single().Status);

        // Tick 2: attempt 2 exhausts the step; the uncaught exception fails the parent (MaxAttempts = 1).
        Assert.Equal(RunOnceOutcome.Failed, await Runtime.RunOnceAsync(enqueued, ct));

        var step = Assert.Single(await ReadStepsAsync(enqueued.JobId, ct));
        Assert.Equal(JobStepStatusCode.Exhausted, step.Status);
        Assert.Null(step.NextRetryAtUtc);
        Assert.NotNull(step.ReasonCode);

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Failed, job.Status);
        var finishedEvent = await ReadLatestEventAsync(enqueued.JobId, EventCode.JobExecutionFinished, ct);
        Assert.Equal(JobEventReasonCode.JobUnhandledException, finishedEvent.ReasonCode);
        Assert.Equal(2, JobStepProbes.BodyInvocations[enqueued.JobId]);
    }

    [Fact(DisplayName = "CompleteStep loses the CAS and reports StaleVersion when the slot advanced under another execution")]
    public async Task CompleteStep_returns_stale_version_when_the_slot_advanced_under_another_execution()
    {
        var ct = TestContext.Current.CancellationToken;

        // A real job row to own the step slot (enqueue only; never run).
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-step-basic", JobPayload.None), ct);

        // First start hands out the version a handler would carry; a second start advances the slot,
        // modelling a concurrent execution of the same job that re-entered the step.
        var first = await Services.GetRequiredService<IExecutionStore>().StartStepAsync(enqueued.JobId, "compute", atMostOnce: false, ct);
        Assert.Equal(StartStepOutcomeCode.Invoke, first.Outcome);
        var second = await Services.GetRequiredService<IExecutionStore>().StartStepAsync(enqueued.JobId, "compute", atMostOnce: false, ct);
        Assert.Equal(StartStepOutcomeCode.Invoke, second.Outcome);
        Assert.NotEqual(first.Version, second.Version);

        // Completing on the stale (first) version must lose the CAS and report it - not a phantom success.
        var stale = await Services
            .GetRequiredService<IExecutionStore>()
            .CompleteStepAsync(
                new CompleteStepCommand(
                    enqueued.JobId,
                    "compute",
                    Succeeded: true,
                    ResultFormatId: 0,
                    Result: null,
                    ReasonCode: null,
                    ReasonMessage: null,
                    DelaySeconds: 0,
                    MaxAttempts: 5,
                    RetryWindowSeconds: null,
                    ExpectedVersion: first.Version
                ),
                ct
            );

        Assert.Equal(CompleteStepOutcomeCode.StaleVersion, stale.Outcome);

        // The losing completion changed nothing: the slot is still Pending at the advanced version.
        var slot = Assert.Single(await ReadStepsAsync(enqueued.JobId, ct));
        Assert.Equal(JobStepStatusCode.Pending, slot.Status);
        Assert.Equal(second.Version, slot.Version);
        Assert.Null(slot.Result);
    }

    [Fact(DisplayName = "CompleteStep reports StaleVersion rather than throwing when the slot row is absent")]
    public async Task CompleteStep_returns_stale_version_when_the_slot_row_is_absent()
    {
        var ct = TestContext.Current.CancellationToken;

        // A real job row owns no step slot (StartStep never ran, or the slot was reset away).
        // A completion arriving for an absent slot must report StaleVersion, not throw - the routine
        // always yields one decision row regardless of whether a matching slot exists.
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-step-basic", JobPayload.None), ct);

        var outcome = await Services
            .GetRequiredService<IExecutionStore>()
            .CompleteStepAsync(
                new CompleteStepCommand(
                    enqueued.JobId,
                    "compute",
                    Succeeded: true,
                    ResultFormatId: 0,
                    Result: null,
                    ReasonCode: null,
                    ReasonMessage: null,
                    DelaySeconds: 0,
                    MaxAttempts: 5,
                    RetryWindowSeconds: null,
                    ExpectedVersion: 1
                ),
                ct
            );

        Assert.Equal(CompleteStepOutcomeCode.StaleVersion, outcome.Outcome);
        Assert.Null(outcome.NextRetryAtUtc);
        Assert.Empty(await ReadStepsAsync(enqueued.JobId, ct));
    }

    // ---------- helpers ----------

    private async Task<IReadOnlyList<JobStep>> ReadStepsAsync(long jobId, CancellationToken ct)
    {
        return await Db.From<JobStep>().Where(a => a.JobId == jobId).ToListAsync(ct);
    }
}
