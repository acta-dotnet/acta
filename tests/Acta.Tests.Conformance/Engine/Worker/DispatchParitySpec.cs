using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Worker;

/// <summary>
/// Exactly-once under each <see cref="ExecutionProfile"/>: the claimer and its executors drain a backlog with every
/// job run once. Runs the pipeline until the LoadExecutionCounter has one count per job, then asserts the count equals
/// the enqueued total: a single O(1) exactly-once plus full-drain proof. Each profile is its own contract below.
/// </summary>
public abstract class DispatchParitySpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    private const int JobCount = 50;

    private const int Executors = 8;

    /// <summary>The execution profile the pipeline runs under.</summary>
    protected abstract ExecutionProfile Profile { get; }

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        services.AddSingleton<LoadExecutionCounter>();
        services.Configure<JobsOptions>(o =>
        {
            o.ExecutionProfile = Profile;
            o.MaxConcurrentExecutors = Executors;
        });
    }

    protected async Task DrainsABacklogExactlyOnceAsync(CancellationToken ct)
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
}

[ConformanceSpec(
    "runtime.combined-dispatch-parity",
    "The Direct pipeline drains a backlog exactly once",
    Area = "Execution",
    Contract = "Under ExecutionProfile.Direct the claimer and its executors drain a backlog exactly once, claiming Ready to Executing in one round-trip."
)]
public sealed class DirectDispatchParitySpec : DispatchParitySpec
{
    protected override ExecutionProfile Profile => ExecutionProfile.Direct;

    [Fact(DisplayName = "Direct: every enqueued job executes exactly once and the whole backlog drains to completion")]
    public Task Drains_a_backlog_exactly_once() => DrainsABacklogExactlyOnceAsync(TestContext.Current.CancellationToken);
}

[ConformanceSpec(
    "runtime.buffered-dispatch-parity",
    "The Buffered pipeline drains a backlog exactly once",
    Area = "Execution",
    Contract = "Under ExecutionProfile.Buffered the claimer and its executors drain a backlog exactly once, starting each Dispatched claim before it runs."
)]
public sealed class BufferedDispatchParitySpec : DispatchParitySpec
{
    protected override ExecutionProfile Profile => ExecutionProfile.Buffered;

    [Fact(DisplayName = "Buffered: every enqueued job executes exactly once and the whole backlog drains to completion")]
    public Task Drains_a_backlog_exactly_once() => DrainsABacklogExactlyOnceAsync(TestContext.Current.CancellationToken);
}
