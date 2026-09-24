using Acta.Runtime.Modules.Execution;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Acta.Tests.Execution;

/// <summary>
/// The window between claim and start: the worker holds a claim it has not begun executing, and
/// <c>start_execution</c> answers anything but <c>Started</c> - the row was reclaimed by recovery,
/// reassigned to another worker, or moved out of Dispatched by an operator control verb. The contract is
/// a clean skip: the CAS guard (including the claim-time version) means nothing was mutated, so the
/// attempt must invoke no handler, submit no completion, and report NothingClaimed so the row is simply
/// re-claimed on a later tick. A lapsed lease on a row still this worker's is the exception: nothing
/// took the row, the heartbeat keeps renewing it, and the start is retried until it lands. The sibling
/// window - losing the claim DURING the handler, at the completion CAS - is
/// <c>JobExecutionOwnershipTests</c> and <c>WorkerCrashRecoveryChaosSpec</c>.
/// </summary>
public sealed class JobExecutionLostClaimTests
{
    [Fact]
    public Task A_lease_reclaimed_between_claim_and_start_is_a_clean_skip() => AssertCleanSkip(StartExecutionAction.LostClaim);

    [Fact]
    public Task A_row_reassigned_to_another_worker_between_claim_and_start_is_a_clean_skip() =>
        AssertCleanSkip(StartExecutionAction.NotOwner);

    [Fact]
    public Task An_operator_control_landing_between_claim_and_start_is_a_clean_skip() =>
        AssertCleanSkip(StartExecutionAction.AlreadyTerminal);

    [Fact]
    public async Task An_expired_lease_on_a_row_still_this_workers_is_retried_until_the_lease_is_renewed()
    {
        // Expiry moves no version and clears no owner, and the heartbeat renews every row this worker
        // holds from database state: a skip here would leave a row nothing progresses and nothing
        // reclaims. The start is paced and retried at the same version until it lands.
        var harness = new JobExecutionHarness(
            startAction: StartExecutionAction.LeaseExpired,
            startAfterFailure: StartExecutionAction.Started,
            rowAfterLostClaim: JobExecutionHarness.Row(JobStatusCode.Dispatched, version: 1)
        );

        var outcome = await harness.RunAsync();

        Assert.Equal(RunOnceOutcome.Completed, outcome);
        Assert.True(harness.HandlerRan);
        Assert.Equal([1, 1], harness.StartVersions);
    }

    [Fact]
    public async Task An_expired_lease_on_a_row_recovery_took_is_a_clean_skip()
    {
        // The positive control: recovery reclaimed the row between the refusal and the read, so the
        // row is no longer this worker's and skipping it is safe.
        var harness = new JobExecutionHarness(
            startAction: StartExecutionAction.LeaseExpired,
            rowAfterLostClaim: JobExecutionHarness.Row(JobStatusCode.Ready, version: 2, leasedByWorkerId: 8)
        );

        var outcome = await harness.RunAsync();

        Assert.Equal(RunOnceOutcome.NothingClaimed, outcome);
        Assert.False(harness.HandlerRan);
        Assert.Empty(harness.Submitted);
        Assert.Equal([1], harness.StartVersions);
    }

    [Fact]
    public async Task An_AlreadyTerminal_answer_on_a_row_still_this_workers_is_retried_at_the_rows_version()
    {
        // PostgreSQL classifies a start that waited on a row lock against its statement snapshot: a
        // reprioritize that committed meanwhile leaves the row Dispatched under this worker at a moved
        // version while the answer says AlreadyTerminal. The row, not the answer, decides.
        var harness = new JobExecutionHarness(
            startAction: StartExecutionAction.AlreadyTerminal,
            startAfterFailure: StartExecutionAction.Started,
            rowAfterLostClaim: JobExecutionHarness.Row(JobStatusCode.Dispatched, version: 2)
        );

        var outcome = await harness.RunAsync();

        Assert.Equal(RunOnceOutcome.Completed, outcome);
        Assert.True(harness.HandlerRan);
        Assert.Equal([1, 2], harness.StartVersions);
    }

    private static async Task AssertCleanSkip(StartExecutionAction start)
    {
        var harness = new JobExecutionHarness(startAction: start);

        var outcome = await harness.RunAsync();

        // Nothing was mutated and nothing must be: no handler invocation, and above all no completion
        // command, because a completion CAS submitted for a row this worker no longer owns is how a
        // reclaimed job gets a second, contradictory terminal write attempted against it.
        Assert.Equal(RunOnceOutcome.NothingClaimed, outcome);
        Assert.False(harness.HandlerRan);
        Assert.Empty(harness.Submitted);

        // A skip is routine, not a fault: it is recorded at Information, and it names which of the four
        // ways the claim was lost, because that is the only place an operator can tell an expired lease
        // from a control verb after the fact.
        var entry = Assert.Single(harness.Log);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("lost claim on job 4242", entry.Message, StringComparison.Ordinal);
        Assert.Contains("execution number 3", entry.Message, StringComparison.Ordinal);
        Assert.Contains(start.ToString(), entry.Message, StringComparison.Ordinal);
    }
}
