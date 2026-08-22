using Acta.Runtime.Modules.Execution;
using Xunit;

namespace Acta.Tests.Execution;

/// <summary>
/// Unit pins for the step-ownership path through <see cref="JobExecution.RunAsync"/>, driven by
/// <see cref="JobExecutionHarness"/>: a <c>complete_step</c> StaleVersion on a job this attempt still
/// owns re-arms as a retryable abort instead of landing terminal Failed with no reason, no completion
/// shape ever submits Failed without a reason, and a completion CAS that answers NotOwner writes
/// nothing at all.
/// </summary>
public sealed class JobExecutionOwnershipTests
{
    [Fact]
    public async Task Stale_step_version_on_an_owned_job_re_arms_as_a_retryable_abort()
    {
        var harness = new JobExecutionHarness(stepOutcome: CompleteStepOutcomeCode.StaleVersion);

        var outcome = await harness.RunAsync();

        var completion = harness.Completion;
        Assert.Equal(RunOnceOutcome.Rearmed, outcome);
        Assert.Equal(ExecutionOutcome.Failed, completion.Outcome);
        Assert.Equal(JobEventReasonCode.JobAttemptAborted, completion.JobEventReasonCode);
        Assert.Contains(JobExecutionHarness.StepName, completion.ReasonMessage);

        // The re-arm shape, not a terminal one: the execution row records Rescheduled, the job goes
        // back to Ready with a backoff delay, and the bumped failure count is what the budget reads
        // on the next attempt. A terminal landing would carry none of these and no HandlerStatusCode.
        Assert.Equal((byte)ExecutionStatusCode.Rescheduled, completion.RescheduleStatusCode);
        Assert.NotNull(completion.RescheduleDelaySeconds);
        Assert.Equal((short)1, completion.FailureCount);
        Assert.Null(completion.HandlerStatusCode);
        Assert.Null(completion.FinalStatus);
    }

    [Fact]
    public async Task Stale_step_version_recording_a_failed_body_aborts_rather_than_reporting_the_body()
    {
        // The other half of the ownership pair, and the one no test reached: the step body threw, and the
        // CAS that would have RECORDED that failure found a stale version. Nothing durable was written,
        // so the business failure is not this attempt's to report - reporting JobUnhandledException here
        // would attribute a failure the ledger never accepted, and burn a retry against a budget the
        // routine never decremented. The abort must win over both the retry signal and exhaustion.
        var harness = new JobExecutionHarness(stepOutcome: CompleteStepOutcomeCode.StaleVersion);

        var outcome = await harness.RunAsync(
            static (ctx, token) =>
                ctx.RunStepAsync(
                    JobExecutionHarness.StepName,
                    static _ => Task.FromException(new InvalidOperationException("boom")),
                    ct: token
                )
        );

        var completion = harness.Completion;
        Assert.Equal(RunOnceOutcome.Rearmed, outcome);
        Assert.Equal(ExecutionOutcome.Failed, completion.Outcome);
        Assert.Equal(JobEventReasonCode.JobAttemptAborted, completion.JobEventReasonCode);
        Assert.Contains(JobExecutionHarness.StepName, completion.ReasonMessage);
        Assert.DoesNotContain("boom", completion.ReasonMessage, StringComparison.Ordinal);
        Assert.Equal((byte)ExecutionStatusCode.Rescheduled, completion.RescheduleStatusCode);
    }

    [Fact]
    public async Task Stale_step_version_past_the_budget_lands_terminal_with_the_reason_kept()
    {
        // Budget of one: the same abort that re-armed above is out of retries, so it lands terminal.
        // The reason must survive that transition, which is the fact the old bug lost.
        var harness = new JobExecutionHarness(stepOutcome: CompleteStepOutcomeCode.StaleVersion, maxAttempts: 1);

        var outcome = await harness.RunAsync();

        var completion = harness.Completion;
        Assert.Equal(RunOnceOutcome.Failed, outcome);
        Assert.Equal(ExecutionOutcome.Failed, completion.Outcome);
        Assert.Equal(JobEventReasonCode.JobAttemptAborted, completion.JobEventReasonCode);
        Assert.Contains(JobExecutionHarness.StepName, completion.ReasonMessage);
        Assert.Null(completion.RescheduleStatusCode);
    }

    [Fact]
    public async Task No_completion_path_submits_a_failed_outcome_with_no_reason()
    {
        // The regression guard: walk every completion command the four reachable failure-and-success
        // shapes produce and require a classified reason on each Failed one. A reason-less Failed is
        // what made the ownership incident land a recoverable job in terminal Failed with a blank
        // timeline entry.
        var submitted = new List<CompleteExecutionRequest>();

        submitted.AddRange(await Submissions(new JobExecutionHarness(stepOutcome: CompleteStepOutcomeCode.StaleVersion)));
        submitted.AddRange(await Submissions(new JobExecutionHarness(stepOutcome: CompleteStepOutcomeCode.StaleVersion, maxAttempts: 1)));
        submitted.AddRange(
            await Submissions(new JobExecutionHarness(maxAttempts: 1), static (_, _) => throw new InvalidOperationException("boom"))
        );
        submitted.AddRange(await Submissions(new JobExecutionHarness()));

        Assert.Equal(4, submitted.Count);
        Assert.Contains(submitted, c => c.Outcome == ExecutionOutcome.Succeeded);
        foreach (var completion in submitted.Where(c => c.Outcome == ExecutionOutcome.Failed))
        {
            Assert.NotNull(completion.JobEventReasonCode);
            Assert.False(string.IsNullOrWhiteSpace(completion.ReasonMessage));
        }
    }

    [Fact]
    public async Task Completion_cas_answering_not_owner_leaves_no_terminal_write()
    {
        // True loss: the slot was stolen and the heartbeat cancelled the attempt token, so the
        // completion CAS matches no row. The runner still submits the retryable re-arm command (an
        // owned row must be re-armed rather than skipped), the store applies none of it, and the
        // attempt reports NothingClaimed.
        var harness = new JobExecutionHarness(
            stepOutcome: CompleteStepOutcomeCode.StaleVersion,
            completionAction: CompleteExecutionAction.NotOwner,
            cancelAttemptOnStepCompletion: true
        );

        var outcome = await harness.RunAsync();

        Assert.Equal(RunOnceOutcome.NothingClaimed, outcome);
        Assert.Empty(harness.Applied);
        Assert.Equal((byte)ExecutionStatusCode.Rescheduled, harness.Completion.RescheduleStatusCode);
    }

    [Fact]
    public async Task Not_owner_without_a_cancelled_attempt_stays_an_anomaly()
    {
        // The same NotOwner answer with the attempt token still live is the genuine anomaly the
        // runner refuses to swallow: losing the row without any cancellation means the ledger and
        // this worker disagree. Pinned so a future "just skip it" cannot land unnoticed.
        var harness = new JobExecutionHarness(
            stepOutcome: CompleteStepOutcomeCode.StaleVersion,
            completionAction: CompleteExecutionAction.NotOwner
        );

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.RunAsync());

        Assert.Empty(harness.Applied);
    }

    private static async Task<IReadOnlyList<CompleteExecutionRequest>> Submissions(
        JobExecutionHarness harness,
        Func<JobContext, CancellationToken, Task>? handler = null
    )
    {
        await harness.RunAsync(handler);
        return harness.Submitted;
    }
}
