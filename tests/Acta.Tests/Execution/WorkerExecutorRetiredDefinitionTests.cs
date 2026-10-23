using Xunit;

namespace Acta.Tests.Execution;

/// <summary>
/// A job whose definition is retired never runs again: a claim of it lands Cancelled unrun, and an attempt in flight
/// when the worker learns of the retire finishes but lands Cancelled where it would otherwise re-arm.
/// </summary>
public sealed class WorkerExecutorRetiredDefinitionTests
{
    [Fact]
    public async Task A_claim_of_a_retired_definition_lands_cancelled_without_running()
    {
        var harness = new WorkerExecutorHarness();
        harness.RetireDefinition();

        Assert.Equal(RunOnceStatus.Completed, await harness.RunAsync());

        Assert.False(harness.HandlerRan);
        AssertCancelledByRetirement(harness.Completion);
    }

    [Fact]
    public async Task A_claim_of_a_retired_definition_with_no_handler_here_lands_cancelled_instead_of_bouncing()
    {
        var harness = new WorkerExecutorHarness();
        harness.RetireDefinition();

        Assert.Equal(RunOnceStatus.Completed, await harness.RunWithNoDescriptorAsync());

        AssertCancelledByRetirement(harness.Completion);
        Assert.DoesNotContain(harness.Log, entry => entry.Message.Contains("no handler in this deployment", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_attempt_failing_inside_its_budget_after_the_retire_lands_cancelled_instead_of_retrying()
    {
        var harness = new WorkerExecutorHarness(maxAttempts: 3);

        var outcome = await harness.RunAsync(
            (_, _) =>
            {
                harness.RetireDefinition();
                throw new InvalidOperationException("fails after the retire");
            }
        );

        Assert.Equal(RunOnceStatus.Completed, outcome);
        AssertCancelledByRetirement(harness.Completion);
    }

    private static void AssertCancelledByRetirement(BoundCompletion completion)
    {
        Assert.Equal((byte)JobStatusCode.Cancelled, completion.HandlerStatusCode);
        Assert.Equal(EventReason.DefinitionRetired, completion.Reason);
        Assert.Null(completion.RescheduleEventCode);
        Assert.Null(completion.FailureCount);
    }
}
