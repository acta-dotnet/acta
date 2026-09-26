using Acta.Runtime.Modules.Execution;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Acta.Tests.Execution;

/// <summary>
/// A claim the deployment has no descriptor for. Claims are selected by namespace, so this is not an
/// exotic state: a manifest that dropped a definition whose jobs remain, and every rolling deploy,
/// produces it. The contract is that the executor releases the claim instead of refusing it, because
/// the claim has already stamped the lease and the heartbeat renews leased rows from database state
/// alone - a refusal strands the job under a healthy worker forever.
/// </summary>
public sealed class JobExecutorUnsupportedClaimTests
{
    [Fact]
    public async Task An_unsupported_claim_is_released_as_a_budget_neutral_rearm()
    {
        var harness = new JobExecutionHarness();

        var outcome = await harness.RunWithNoDescriptorAsync();

        Assert.Equal(RunOnceOutcome.Rearmed, outcome);
        Assert.False(harness.HandlerRan);

        var released = harness.Completion;
        Assert.Equal(ExecutionOutcome.Rescheduled, released.Outcome);
        Assert.Equal((byte)ExecutionStatusCode.Rescheduled, released.RescheduleStatusCode);

        // Budget-neutral, so a definition that stays missing cannot burn the job's retries: no failure
        // count is submitted and complete_execution's COALESCE leaves the stored one alone.
        Assert.Null(released.FailureCount);
        Assert.Null(released.HandlerStatusCode);

        // Held back by the safety-poll interval, the bound on how long a Ready row waits to be found
        // by a process this one shares no wakeup transport with - which is who has to run this job.
        Assert.Equal((int)new JobsOptions().SafetyPollInterval.TotalSeconds, released.RescheduleDelaySeconds);
        Assert.Null(released.RescheduleResumeAtUtc);

        // No catalog reason describes a worker declining a claim; Unclassified says the story is in the
        // message, and the message names the definition so an operator can see which handler is missing.
        Assert.Equal(JobEventReasonCode.Unclassified, released.JobEventReasonCode);
        Assert.NotNull(released.ReasonMessage);
        Assert.Contains("definition_id=1", released.ReasonMessage!, StringComparison.Ordinal);

        // One warning per bounce, so an incapable worker cycling a job is visible in a log an operator
        // already reads rather than only in the event ledger.
        var warning = Assert.Single(harness.Log, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("no handler in this deployment", warning.Message, StringComparison.Ordinal);
        Assert.Contains("definition_id 1", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_provider_error_on_the_start_write_is_retried_before_the_release()
    {
        var harness = new JobExecutionHarness(startFailsOnce: true);

        var outcome = await harness.RunWithNoDescriptorAsync();

        // The start CAS is repeated like the completion: one dropped connection must not escape to
        // the worker loop, which would leave the Dispatched row under a lease the heartbeat renews.
        Assert.Equal(RunOnceOutcome.Rearmed, outcome);
        Assert.Equal(2, harness.StartAttempts);
        Assert.Equal(ExecutionOutcome.Rescheduled, harness.Completion.Outcome);
    }

    [Fact]
    public async Task A_start_that_committed_and_lost_its_response_is_still_released()
    {
        // The retry resubmits the stale version and hears LostClaim; the row says Executing under this
        // worker at this execution number, which is a start that committed, so the release lands.
        var harness = new JobExecutionHarness(
            startFailsOnce: true,
            startAfterFailure: StartExecutionAction.LostClaim,
            rowAfterLostClaim: JobExecutionHarness.Row(JobStatusCode.Executing, version: 2)
        );

        var outcome = await harness.RunWithNoDescriptorAsync();

        Assert.Equal(RunOnceOutcome.Rearmed, outcome);
        Assert.Equal(2, harness.StartAttempts);
        Assert.Equal(ExecutionOutcome.Rescheduled, harness.Completion.Outcome);
    }

    [Fact]
    public async Task A_version_moved_under_the_claim_is_retried_against_the_row()
    {
        // An operator verb bumped the version of the Dispatched row between the claim and the start, so
        // the first start hears LostClaim on a row that is still this worker's; the second carries the
        // version the row holds now and lands.
        var harness = new JobExecutionHarness(
            startAction: StartExecutionAction.LostClaim,
            startAfterFailure: StartExecutionAction.Started,
            rowAfterLostClaim: JobExecutionHarness.Row(JobStatusCode.Dispatched, version: 2)
        );

        var outcome = await harness.RunWithNoDescriptorAsync();

        Assert.Equal(RunOnceOutcome.Rearmed, outcome);
        Assert.Equal([1, 2], harness.StartVersions);
        Assert.Equal(ExecutionOutcome.Rescheduled, harness.Completion.Outcome);
    }

    [Fact]
    public async Task A_claim_lost_before_the_release_writes_nothing()
    {
        // The row was reclaimed, reassigned, or moved by a control verb between claim and release: it
        // reads as another worker's. Submitting a completion for a row this worker no longer owns is
        // exactly what the start CAS exists to prevent, so the release is abandoned, not forced.
        var harness = new JobExecutionHarness(
            startAction: StartExecutionAction.LostClaim,
            rowAfterLostClaim: JobExecutionHarness.Row(JobStatusCode.Executing, version: 2, leasedByWorkerId: 8)
        );

        var outcome = await harness.RunWithNoDescriptorAsync();

        Assert.Equal(RunOnceOutcome.NothingClaimed, outcome);
        Assert.Equal(1, harness.StartAttempts);
        Assert.Empty(harness.Submitted);
    }
}
