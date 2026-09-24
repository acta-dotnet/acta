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

    [Fact]
    public async Task A_row_whose_current_execution_has_a_pending_bulk_completion_is_left_to_that_completion()
    {
        // The Bulk executor has returned and its attempt is gone, but the completion it buffered is still
        // being written: rescheduling the row now would make that completion lose its CAS and run the
        // handler again.
        var harness = new JobExecutionHarness(rowAfterLostClaim: JobExecutionHarness.Row(JobStatusCode.Executing, version: 2));

        var context = await harness.ReleaseOrphanWithPendingCompletionAsync(pendingExecutionNumber: 3);

        Assert.Empty(harness.Submitted);
        Assert.Contains((4242L, 3), context.PendingCompletions.Keys);
    }

    [Fact]
    public async Task A_pending_completion_for_an_older_execution_neither_shields_the_row_nor_is_touched_by_its_release()
    {
        // The row has moved on to execution 4 under this worker with nothing behind it; the completion
        // still pending for execution 3 speaks for execution 3 alone.
        var harness = new JobExecutionHarness(
            rowAfterLostClaim: JobExecutionHarness.Row(JobStatusCode.Executing, version: 5, executionNumber: 4)
        );

        var context = await harness.ReleaseOrphanWithPendingCompletionAsync(pendingExecutionNumber: 3);

        Assert.Equal(ExecutionOutcome.Rescheduled, harness.Completion.Outcome);
        Assert.Equal(4, harness.Completion.ExpectedExecutionNumber);
        Assert.Contains((4242L, 3), context.PendingCompletions.Keys);
    }
}
