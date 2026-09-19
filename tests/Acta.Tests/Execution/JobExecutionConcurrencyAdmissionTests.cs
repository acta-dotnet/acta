using Acta.Runtime.Modules.Execution;
using Xunit;

namespace Acta.Tests.Execution;

/// <summary>
/// Unit pins for concurrency admission in <see cref="JobExecution.RunAsync"/>, driven by
/// <see cref="JobExecutionHarness"/>'s scripted slot store: which key and how many slots the runner
/// asks for, that a granted slot is released once the handler is done, and that a denied slot skips
/// the handler and settles the attempt as a budget-neutral re-arm with the bounce delay.
/// </summary>
public sealed class JobExecutionConcurrencyAdmissionTests
{
    [Fact]
    public async Task No_key_and_no_limit_takes_no_slot_at_all()
    {
        var harness = new JobExecutionHarness();

        var outcome = await harness.RunAsync();

        Assert.Equal(RunOnceOutcome.Completed, outcome);
        Assert.Empty(harness.SlotRequests);
    }

    [Fact]
    public async Task A_persisted_key_with_no_limit_asks_for_one_slot_on_that_key()
    {
        // The pre-limit mutex, unchanged in meaning: the enqueue's key, a single slot.
        var harness = new JobExecutionHarness(concurrencyKey: "customer-1");

        var outcome = await harness.RunAsync();

        var request = Assert.Single(harness.SlotRequests);
        Assert.Equal(RunOnceOutcome.Completed, outcome);
        Assert.Equal("1.sem.customer-1", request.KeyPrefix);
        Assert.Equal(1, request.Limit);
        Assert.Equal(1, harness.SlotReleases);
    }

    [Fact]
    public async Task A_limit_with_no_key_gates_on_the_definition_name()
    {
        var harness = new JobExecutionHarness(concurrencyLimit: 4);

        await harness.RunAsync();

        var request = Assert.Single(harness.SlotRequests);
        Assert.Equal("1.sem.harness-job", request.KeyPrefix);
        Assert.Equal(4, request.Limit);
    }

    [Fact]
    public async Task A_persisted_key_and_a_limit_gate_on_the_key_with_the_definitions_slot_count()
    {
        var harness = new JobExecutionHarness(concurrencyKey: "customer-1", concurrencyLimit: 4);

        await harness.RunAsync();

        var request = Assert.Single(harness.SlotRequests);
        Assert.Equal("1.sem.customer-1", request.KeyPrefix);
        Assert.Equal(4, request.Limit);
    }

    [Fact]
    public async Task A_denied_slot_skips_the_handler_and_re_arms_budget_neutral()
    {
        var harness = new JobExecutionHarness(concurrencyKey: "customer-1", slotGranted: false);

        var outcome = await harness.RunAsync();

        var completion = harness.Completion;
        Assert.Equal(RunOnceOutcome.Rearmed, outcome);
        Assert.False(harness.HandlerRan);
        Assert.Equal(ExecutionOutcome.Rescheduled, completion.Outcome);
        Assert.Equal(JobEventReasonCode.JobConcurrencyKeyHeld, completion.JobEventReasonCode);

        // Budget-neutral: the fixed bounce delay re-arms the row and no failure count is written at
        // all, so the next attempt reads the budget exactly as this one found it.
        Assert.Equal(new JobsOptions().ConcurrencyKeyBounceDelaySeconds, completion.RescheduleDelaySeconds);
        Assert.Null(completion.FailureCount);

        // Nothing was taken, so nothing is released; a release here would free another holder's slot.
        Assert.Equal(0, harness.SlotReleases);
    }
}
