using Acta.Runtime.Modules.Execution;
using Xunit;

namespace Acta.Tests.Execution;

/// <summary>
/// Pins the two per-attempt inputs <see cref="JobExecutionHarness"/> used to leave at their optional
/// defaults, each of which silently disabled a production behavior in every test built on the harness:
/// the inline payload cap (defaulted to <c>int.MaxValue</c>, so a handler write that production
/// rejects went through) and the running attempt (defaulted to null, so every execution timeout read
/// back as a plain external cancel).
/// </summary>
public sealed class JobExecutionAttemptContextTests
{
    [Fact]
    public async Task Handler_writes_are_capped_at_the_configured_inline_payload_size()
    {
        var harness = new JobExecutionHarness();
        var oversized = new string('x', 2 * 1024 * 1024);
        PayloadTooLargeException? rejected = null;

        var outcome = await harness.RunAsync(
            async (ctx, ct) =>
            {
                rejected = await Assert.ThrowsAsync<PayloadTooLargeException>(() => ctx.SetVariableAsync("receipt", oversized, ct));
            }
        );

        Assert.Equal(RunOnceOutcome.Completed, outcome);
        Assert.NotNull(rejected);

        // The cap the attempt enforces is the configured one, not an unbounded harness default.
        Assert.Equal(new JobsOptions().MaxInlinePayloadBytes, rejected!.MaxBytes);
        Assert.Contains("receipt", rejected.EntryPoint);
    }

    [Fact]
    public async Task An_execution_timeout_is_recorded_as_a_timeout_not_an_aborted_attempt()
    {
        var harness = new JobExecutionHarness();

        var outcome = await harness.RunAsync(
            async (_, ct) =>
            {
                // The watchdog fires the attempt's execution timeout; the handler observes its token the
                // way a cooperative handler does.
                harness.TimeOutAttempt();
                await Task.Delay(Timeout.Infinite, ct);
            }
        );

        var completion = harness.Completion;
        Assert.Equal(RunOnceOutcome.Rearmed, outcome);
        Assert.Equal(ExecutionOutcome.Failed, completion.Outcome);

        // Without the running attempt the runner cannot tell a timeout from an external cancel and
        // files this as JobAttemptAborted, which is what blinded every timeout test on this harness.
        Assert.Equal(JobEventReasonCode.JobExecutionTimeout, completion.JobEventReasonCode);
    }
}
