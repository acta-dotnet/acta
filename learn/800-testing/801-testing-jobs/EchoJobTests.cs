// How to test a handler with the test host
// `Acta.Testing` runs the real runtime on a real database with no background loop: `RunOnceAsync` decides when a job
// executes, so a test drives one attempt at a time. Run it with `dotnet test learn/800-testing/801-testing-jobs`.
using Acta.Testing;
using Xunit;

namespace Acta.Learn.Testing;

public sealed class EchoJobTests(ActaHostFixture acta) : ActaTestBase(acta)
{
    [Fact]
    public async Task Echo_runs_when_driven_and_returns_the_message()
    {
        var enqueued = await Jobs.EnqueueAsync(new Echo("hello"), ct: Ct);

        // Nothing executes until the test drives a tick.
        Assert.Equal(JobStatusCode.Ready, (await enqueued.GetStatusAsync(Ct))?.Status);

        // learn: 2. Drive one attempt and assert what became of it
        // `RunOnceAsync` runs one attempt of that job and answers its outcome.
        var outcome = await Host.RunOnceAsync(enqueued, Ct);

        Assert.Equal(ActaRunStatus.Completed, outcome);
        // learn: end
        Assert.Equal(JobStatusCode.Succeeded, (await enqueued.GetStatusAsync(Ct))?.Status);
        var result = await enqueued.GetResultAsync<EchoResult>(Ct);
        Assert.Equal("hello", result.Message);
    }

    [Fact]
    public async Task Failing_echo_rearms_per_retry_then_settles_failed()
    {
        var enqueued = await Jobs.EnqueueAsync(new Echo("boom"), ct: Ct);

        // learn: 3. Assert each retry, then the final failure
        // Attempts 1 and 2 throw; with retries left the job re-arms to Ready, not Failed. The job declares
        // `MaxAttempts = 3, Backoff = "0s"`, so each retry is due at once; under the default backoff a test
        // makes it due with `ForceJobDueAsync`, as how-to 803 does.
        Assert.Equal(ActaRunStatus.Rearmed, await Host.RunOnceAsync(enqueued, Ct));
        Assert.Equal(ActaRunStatus.Rearmed, await Host.RunOnceAsync(enqueued, Ct));

        // Attempt 3 spends the MaxAttempts budget: terminal Failed.
        Assert.Equal(ActaRunStatus.Failed, await Host.RunOnceAsync(enqueued, Ct));
        // learn: end
        Assert.Equal(JobStatusCode.Failed, (await enqueued.GetStatusAsync(Ct))?.Status);
    }
}
