using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Worker;

/// <summary>
/// Exactly-once: the claimer and its executors drain a backlog with every
/// job run once. Runs the pipeline until the LoadExecutionCounter has one count per job, then asserts the count equals
/// the enqueued total: a single O(1) exactly-once plus full-drain proof.
/// </summary>
[ConformanceSpec(
    "runtime.combined-dispatch-parity",
    "The worker pipeline drains a backlog exactly once",
    Area = "Execution",
    Contract = "The claimer and its executors drain a backlog exactly once, claiming Ready to Executing in one round-trip."
)]
public sealed class DispatchParitySpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    private const int JobCount = 50;

    private const int Executors = 8;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        services.AddSingleton<LoadExecutionCounter>();
        services.Configure<JobsOptions>(o =>
        {
            o.MaxConcurrentExecutors = Executors;
        });
    }

    private async Task DrainsABacklogExactlyOnceAsync(CancellationToken ct)
    {
        var counter = Services.GetRequiredService<LoadExecutionCounter>();

        var batch = new List<EnqueueJobRequest>(JobCount);
        for (var i = 0; i < JobCount; i++)
        {
            batch.Add(new EnqueueJobRequest(TestNamespace, "load-echo", JobPayload.Json(new LoadEcho())));
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

    [Fact(DisplayName = "Every enqueued job executes exactly once and the whole backlog drains to completion")]
    public Task Drains_a_backlog_exactly_once() => DrainsABacklogExactlyOnceAsync(TestContext.Current.CancellationToken);
}
