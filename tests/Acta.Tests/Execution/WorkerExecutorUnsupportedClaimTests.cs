using Microsoft.Extensions.Logging;
using Xunit;

namespace Acta.Tests.Execution;

/// <summary>
/// A claim the deployment has no descriptor for. Claims are selected by namespace, so this is not an
/// exotic state: a manifest that dropped a definition whose jobs remain, and every rolling deploy,
/// produces it. The contract is that the executor releases the claim instead of refusing it, because
/// the claim has already stamped the lease - a refusal would leave the job waiting out that lease.
/// </summary>
public sealed class WorkerExecutorUnsupportedClaimTests
{
    [Fact]
    public async Task An_unsupported_claim_is_released_as_a_budget_neutral_rearm()
    {
        var harness = new WorkerExecutorHarness();

        var outcome = await harness.RunWithNoDescriptorAsync();

        Assert.Equal(RunOnceStatus.Rearmed, outcome);
        Assert.False(harness.HandlerRan);

        var released = harness.Completion;
        Assert.Equal(CompletionKind.Rescheduled, released.Outcome);
        Assert.Equal(EventCode.JobRescheduled, released.RescheduleEventCode);
        Assert.Equal(EventCode.JobExecutionYielded, released.EventCode);

        // Budget-neutral, so a definition that stays missing cannot burn the job's retries: no failure
        // count is submitted and complete_executions' COALESCE leaves the stored one alone.
        Assert.Null(released.FailureCount);
        Assert.Null(released.HandlerStatusCode);

        // Held back by the safety-poll interval, the bound on how long a Ready row waits to be found
        // by a process this one shares no wakeup transport with - which is who has to run this job.
        Assert.Equal((int)new JobsOptions().SafetyPollInterval.TotalSeconds, released.RescheduleDelaySeconds);
        Assert.Null(released.RescheduleResumeAtUtc);

        // No catalog reason describes a worker declining a claim; Unclassified says the story is in the
        // message, and the message names the definition so an operator can see which handler is missing.
        Assert.Null(released.Reason);
        Assert.NotNull(released.EventMessage);
        Assert.Contains("definition_id=1", released.EventMessage!, StringComparison.Ordinal);

        // One warning per bounce, so an incapable worker cycling a job is visible in a log an operator
        // already reads rather than only in the event ledger.
        var warning = Assert.Single(harness.Log, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("no handler in this deployment", warning.Message, StringComparison.Ordinal);
        Assert.Contains("definition_id 1", warning.Message, StringComparison.Ordinal);
    }
}
