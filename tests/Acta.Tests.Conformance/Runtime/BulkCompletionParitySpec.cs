using Acta.Configuration;
using Acta.Payloads;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

/// <summary>
/// Proves the Bulk execution profile drains a backlog exactly once. Bulk is the combined claim-execute
/// loop (like Direct) plus group-committed completions: plain terminal completions are buffered and
/// flushed by parallel flushers, each group-committing a batch via <c>complete_execution</c> in one
/// transaction. The backlog is larger than the batch size so multiple flushes occur. Asserting one
/// latency sample per enqueued job proves the relaxed-durability buffer still finalizes every job
/// exactly once. On SQLite, Bulk behaves as Direct (no batching); on SqlServer/Postgres it exercises the
/// real group-commit path.
/// </summary>
[ConformanceSpec(
    "runtime.bulk-completion-parity",
    "The Bulk profile group-commits completions and drains a backlog exactly once",
    Area = "Execution",
    Contract = "Under ExecutionProfile.Bulk, plain terminal completions are buffered and group-committed by parallel flushers, and the whole backlog still drains exactly once.",
    Arrange = "A backlog larger than BatchCompletionSize is preloaded under ExecutionProfile.Bulk.",
    Act = "The combined claim-execute loop drains the backlog through the buffered completion sink and its parallel flushers.",
    Assert = "Every enqueued job finalizes exactly once, yielding one latency sample per job."
)]
public abstract class BulkCompletionParitySpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private const int JobCount = 300;

    private const int Executors = 8;

    private const int BatchSize = 50;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        services.AddSingleton<LoadLatencySink>();
        services.Configure<JobsOptions>(o =>
        {
            o.ExecutionProfile = ExecutionProfile.Bulk;
            o.MaxConcurrentExecutors = Executors;
            o.BatchCompletionSize = BatchSize;
        });
    }

    [Fact(DisplayName = "Every enqueued job is group-committed exactly once under Bulk and the whole backlog drains to completion")]
    public async Task Bulk_profile_drains_a_backlog_exactly_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var sink = Services.GetRequiredService<LoadLatencySink>();

        var batch = new List<JobEnqueueRequest>(JobCount);
        for (var i = 0; i < JobCount; i++)
        {
            batch.Add(new JobEnqueueRequest(TestNamespace, "load-echo", JobPayload.Json(new LoadEcho(0))));
        }
        await Jobs.EnqueueBatchAsync(batch, ct);

        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loop = Runtime.RunLoopAsync(loopCts.Token);

        var deadline = TimeSpan.FromMinutes(1);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sink.ElapsedTicks.Count < JobCount && sw.Elapsed < deadline)
        {
            await Task.Delay(25, ct);
        }

        await loopCts.CancelAsync();
        await loop;

        Assert.Equal(JobCount, sink.ElapsedTicks.Count);
    }
}
