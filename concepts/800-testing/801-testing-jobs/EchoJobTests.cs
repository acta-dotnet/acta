// Test [Job] handlers with Acta.Testing: real database, real runtime, deterministic single-tick
// drive. ActaTestHost runs no background loop; RunOnceAsync decides when a job executes.
//
//   dotnet test concepts/800-testing/801-testing-jobs

using Acta.Testing.Hosting;
using Xunit;

namespace Acta.Concepts.Testing;

public sealed class EchoJobTests(ActaHostFixture acta) : ActaTestBase(acta)
{
    [Fact]
    public async Task Echo_runs_when_driven_and_returns_the_message()
    {
        var enqueued = await Jobs.EnqueueAsync(new Echo("hello"), ct: Ct);

        // Nothing executes until the test drives a tick.
        Assert.Equal(JobStatusCode.Ready, await Jobs.GetStatusAsync(enqueued, Ct));

        var outcome = await Host.RunOnceAsync(enqueued, Ct);

        Assert.Equal(ActaRunOutcome.Completed, outcome);
        Assert.Equal(JobStatusCode.Done, await Jobs.GetStatusAsync(enqueued, Ct));
        var result = await Jobs.GetResultAsync<EchoResult>(enqueued, Ct);
        Assert.Equal("hello", result!.Message);
    }

    [Fact]
    public async Task Failing_echo_rearms_per_retry_then_settles_failed()
    {
        var enqueued = await Jobs.EnqueueAsync(new Echo("boom"), ct: Ct);

        // Attempts 1 and 2 throw; with retries left the job re-arms to Ready (due now), not Failed.
        Assert.Equal(ActaRunOutcome.Rearmed, await Host.RunOnceAsync(enqueued, Ct));
        Assert.Equal(ActaRunOutcome.Rearmed, await Host.RunOnceAsync(enqueued, Ct));

        // Attempt 3 spends the MaxAttempts budget: terminal Failed.
        Assert.Equal(ActaRunOutcome.Failed, await Host.RunOnceAsync(enqueued, Ct));
        Assert.Equal(JobStatusCode.Failed, await Jobs.GetStatusAsync(enqueued, Ct));
    }
}
