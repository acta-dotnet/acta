using Xunit;

namespace Acta.Tests.Execution;

/// <summary>
/// A claim that reaches an executor with its lease runway already spent, as a claim queued behind a stalled write
/// lands. Its handler never runs, so the job goes back uncharged rather than waiting for recovery to reclaim it and
/// spend an attempt on work that never started.
/// </summary>
public sealed class WorkerExecutorSpentRunwayTests
{
    [Fact]
    public async Task A_claim_whose_runway_is_spent_goes_back_uncharged_without_running()
    {
        var harness = new WorkerExecutorHarness(maxAttempts: 1);

        Assert.Equal(RunOnceStatus.Rearmed, await harness.RunWithSpentRunwayAsync());
        Assert.False(harness.HandlerRan);

        var released = harness.Completion;
        Assert.Equal(CompletionKind.Rescheduled, released.Outcome);
        Assert.Equal(EventCode.JobRescheduled, released.RescheduleEventCode);
        Assert.Null(released.FailureCount);
        Assert.Null(released.HandlerStatusCode);
        Assert.Contains("runway", released.EventMessage!, StringComparison.Ordinal);
    }
}
