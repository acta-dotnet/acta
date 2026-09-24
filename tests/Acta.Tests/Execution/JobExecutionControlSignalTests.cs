using Acta.Runtime.Modules.Execution;
using Xunit;

namespace Acta.Tests.Execution;

/// <summary>
/// A control signal the runtime does not recognize. The base type is public, so a handler can throw
/// its own subclass; rethrowing it out of the attempt would leave the row Executing under a lease the
/// heartbeat renews from database state, so it lands as a handler exception instead.
/// </summary>
public sealed class JobExecutionControlSignalTests
{
    private sealed class HomeMadeSignal() : JobControlException("a signal the runtime never defined");

    [Fact]
    public async Task An_unrecognized_control_signal_fails_the_attempt_instead_of_escaping()
    {
        var harness = new JobExecutionHarness();

        var outcome = await harness.RunAsync(static (_, _) => throw new HomeMadeSignal());

        var completion = harness.Completion;
        Assert.Equal(RunOnceOutcome.Rearmed, outcome);
        Assert.Equal(ExecutionOutcome.Failed, completion.Outcome);
        Assert.Equal(JobEventReasonCode.JobUnhandledException, completion.JobEventReasonCode);
        Assert.Contains(nameof(HomeMadeSignal), completion.ReasonMessage, StringComparison.Ordinal);
    }
}
