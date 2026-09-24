using Acta.Runtime.Modules.Execution;
using Xunit;

namespace Acta.Tests.Execution;

/// <summary>
/// The orphan release against a claim answer that arrives while the release is under way. Both actors
/// are this worker, so the store's version guard cannot tell them apart: a start that lands after the
/// other actor's start reads as this worker's own lost answer, and the row would be run by one and
/// rescheduled by the other, with a second attempt free to claim it while the first still runs. The
/// in-process owner entry is what keeps one of them out.
/// </summary>
public sealed class JobExecutorOrphanReleaseTests
{
    [Fact]
    public async Task A_claim_answer_landing_during_the_orphan_release_is_skipped_and_the_release_completes()
    {
        var harness = new JobExecutionHarness(rowAfterLostClaim: JobExecutionHarness.Row(JobStatusCode.Dispatched, version: 1));

        var lateClaim = await harness.ReleaseOrphanWithLateClaimAnswerAsync();

        Assert.Equal(RunOnceOutcome.NothingClaimed, lateClaim);
        Assert.False(harness.HandlerRan);
        Assert.Equal(1, harness.StartAttempts);
        Assert.Equal(ExecutionOutcome.Rescheduled, harness.Completion.Outcome);
    }
}
