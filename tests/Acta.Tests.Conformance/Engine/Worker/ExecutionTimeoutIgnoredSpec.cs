using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Worker;

/// <summary>
/// Conformance for a handler that ignores its cancellation token past its execution timeout, the limitation
/// docs/known-limitations.md states under "Execution model". The timeout fires the token and nothing stops the handler:
/// the attempt keeps its key's concurrency slot until the handler returns, and a normal return lands Succeeded with its
/// result, however late.
/// </summary>
[ConformanceSpec(
    "execution-timeout.ignored",
    "A handler that ignores its timeout and returns late is recorded Succeeded",
    Area = "Execution",
    Contract = "A handler that runs past its ExecutionTimeout keeps its concurrency slot until it returns, and a normal return is recorded Succeeded with its result."
)]
[Deploys("add-numbers", "timeout-ignored-probe")]
public sealed class ExecutionTimeoutIgnoredSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<ExecutionTimeoutIgnoredSpec>>
{
    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        // A bounced job is due again at once, so the job behind the key runs as soon as the slot is free.
        services.Configure<JobsOptions>(o => o.ConcurrencyKeyBounceDelaySeconds = 0);
    }

    [Fact(
        DisplayName = "A handler past its timeout keeps its key until it returns, lands Succeeded with its result in one execution, and only then does the job behind its key run"
    )]
    public async Task Handler_that_ignores_its_timeout_keeps_its_key_and_lands_Succeeded()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = Unique("timeout-ignored");
        var follower = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))) { ConcurrencyKey = key },
            ct
        );
        var (head, run) = Assert.NotNull(
            await SpecWaits.RepeatUntilAsync(() => RunPastTimeoutAsync(key, ct), trial => trial is not null, ct)
        );

        try
        {
            // Past its timeout the handler still holds the key's slot, so the job behind the key is turned away unrun.
            Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(follower, ct));
        }
        finally
        {
            TimeoutIgnoredProbe.Release(head.JobId);
        }

        Assert.Equal(RunOnceStatus.Completed, await run.WaitAsync(SpecWaits.Gate, ct));
        var landed = await Jobs.GetAsync(head, ct);
        Assert.NotNull(landed);
        Assert.Equal(JobStatusCode.Succeeded, landed!.Status);
        Assert.Equal(1, landed.ExecutionNumber);
        Assert.Equal(0, landed.FailureCount);
        Assert.Equal(TimeoutIgnoredProbe.Result, await Jobs.GetResultAsync<string>(head, ct));

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(follower, ct));
        Assert.Equal(5, (await Jobs.GetResultAsync<AddNumbersResult>(follower, ct)).Sum);
    }

    // One trial: the head's run, once its handler is running past its fired timeout. The timeout is armed at the claim,
    // before the concurrency slot is taken, so a stalled admission can spend it before the handler starts; that attempt
    // lands Failed without running the handler, and null asks for another trial.
    private async Task<(EnqueueJobResponse Head, Task<RunOnceStatus> Run)?> RunPastTimeoutAsync(string key, CancellationToken ct)
    {
        var head = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "timeout-ignored-probe", JobPayload.None) { ConcurrencyKey = key },
            ct
        );
        var run = Runtime.RunOnceAsync(head, ct);
        if (await Task.WhenAny(TimeoutIgnoredProbe.TokenFired(head.JobId), run).WaitAsync(SpecWaits.Gate, ct) == run)
        {
            await run;
            return null;
        }

        return (head, run);
    }
}
