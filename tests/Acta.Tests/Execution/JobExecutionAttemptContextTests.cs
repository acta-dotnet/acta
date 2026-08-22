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
    // null exercises the harness's own JobsOptions default; the other two are caps no default could
    // coincide with, so the pin fails on any divergence between the value this host configured and
    // the one the attempt enforces, not only on the old unbounded int.MaxValue.
    [Theory]
    [InlineData(null)]
    [InlineData(4096)]
    [InlineData(64 * 1024)]
    public async Task Handler_writes_are_capped_at_the_configured_inline_payload_size(int? configuredCap)
    {
        var expectedCap = configuredCap ?? new JobsOptions().MaxInlinePayloadBytes;
        var harness = new JobExecutionHarness(maxInlinePayloadBytes: configuredCap);
        var oversized = new string('x', expectedCap + 1024);
        PayloadTooLargeException? rejected = null;

        var outcome = await harness.RunAsync(
            async (ctx, ct) =>
            {
                rejected = await Assert.ThrowsAsync<PayloadTooLargeException>(() => ctx.SetVariableAsync("receipt", oversized, ct));
            }
        );

        Assert.Equal(RunOnceOutcome.Completed, outcome);
        Assert.NotNull(rejected);

        // The cap the attempt enforces is the one this host configured, to the byte.
        Assert.Equal(expectedCap, rejected!.MaxBytes);
        Assert.True(rejected.ActualBytes > expectedCap);
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
