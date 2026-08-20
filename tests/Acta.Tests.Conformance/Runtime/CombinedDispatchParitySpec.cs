using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

/// <summary>
/// Proves the combined claim-execute path (ExecutionProfile.Direct) preserves exactly-once. A
/// SemaphoreSlim coordinator claims with StartExecuting=true, so claim_batch transitions
/// Ready->Executing in one round-trip with no buffered-Dispatched window; each claimed row runs to
/// completion on its own Task. Enqueues a modest backlog, runs the loop until the LoadExecutionCounter
/// has one count per job, then asserts the count equals the enqueued total: a single O(1) exactly-once
/// plus full-drain proof. Identical assertions run against SqlServer and Postgres via the provider
/// one-liners.
/// </summary>
[ConformanceSpec(
    "runtime.combined-dispatch-parity",
    "The combined claim-execute loop drains a backlog exactly once",
    Area = "Execution",
    Contract = "Under ExecutionProfile.Direct the combined claim-execute loop drains a backlog exactly once, claiming Ready to Executing in one round-trip.",
    Arrange = "A worker is configured with ExecutionProfile.Direct, 8 concurrent executors, and an execution counter, and a 50-job backlog is enqueued.",
    Act = "The run loop drains the backlog through the combined claim-execute coordinator.",
    Assert = "Every enqueued job executes exactly once, with the counter recording one execution per job."
)]
public abstract class CombinedDispatchParitySpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private const int JobCount = 50;

    private const int Executors = 8;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        services.AddSingleton<LoadExecutionCounter>();
        services.Configure<JobsOptions>(o =>
        {
            o.ExecutionProfile = ExecutionProfile.Direct;
            o.MaxConcurrentExecutors = Executors;
        });
    }

    [Fact(DisplayName = "Every enqueued job executes exactly once via the combined loop and the whole backlog drains to completion")]
    public async Task Combined_loop_drains_a_backlog_exactly_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var counter = Services.GetRequiredService<LoadExecutionCounter>();

        var batch = new List<JobEnqueueRequest>(JobCount);
        for (var i = 0; i < JobCount; i++)
        {
            batch.Add(new JobEnqueueRequest(TestNamespace, "load-echo", JobPayload.Json(new LoadEcho())));
        }
        await Jobs.EnqueueBatchAsync(batch, ct);

        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loop = Runtime.RunLoopAsync(loopCts.Token);

        var deadline = SpecWaits.Converge;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (counter.Executions < JobCount && sw.Elapsed < deadline)
        {
            await Task.Delay(25, ct);
        }

        await loopCts.CancelAsync();
        await loop;

        Assert.Equal(JobCount, counter.Executions);
    }
}
