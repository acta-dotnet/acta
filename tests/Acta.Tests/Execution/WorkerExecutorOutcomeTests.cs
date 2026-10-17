using Xunit;

namespace Acta.Tests.Execution;

/// <summary>
/// Pins which outcome an attempt lands when something other than the handler's return decides it: a service that
/// throws while the attempt's scope is disposed after the outcome is decided.
/// </summary>
public sealed class WorkerExecutorOutcomeTests
{
    [Fact]
    public async Task A_service_that_throws_on_disposal_leaves_the_decided_outcome_standing()
    {
        var harness = new WorkerExecutorHarness(scopeDisposalThrows: true);

        var outcome = await harness.RunAsync();

        Assert.Equal(RunOnceStatus.Completed, outcome);
        Assert.True(harness.Completion.Succeeded);
        Assert.Contains(harness.Log, entry => entry.Message.Contains("disposing the attempt's services threw", StringComparison.Ordinal));
    }
}
