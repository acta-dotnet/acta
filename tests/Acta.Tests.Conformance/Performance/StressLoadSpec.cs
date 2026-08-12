using System.Diagnostics;
using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Services.Time;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Performance;

/// <summary>
/// Bulk-preloads a backlog, drains it through the real batch-claim/dispatch loop under N concurrent
/// executors, and reports enqueue throughput, completion throughput, and end-to-end latency percentiles.
/// Asserts every job runs exactly once, so it doubles as a concurrent-claim correctness check.
/// </summary>
/// <remarks>
/// Sizing comes from <c>ACTA_LOAD_JOBS</c> (default 200) and <c>ACTA_LOAD_EXECUTORS</c> (default 8),
/// with a fixed 32-row claim batch. The default size runs in the normal suite as a correctness and
/// contention probe; heavy runs are reached by raising the env vars. Directional probe, not a benchmark.
/// </remarks>
[ConformanceSpec(
    "stress.batch-claim-drains",
    "A backlog drains exactly-once under N concurrent executors with batch claiming",
    Area = "Execution",
    Contract = "A backlog enqueued through IJobs drains to Succeeded exactly once under concurrent batch-claiming executors.",
    Arrange = "A backlog of ACTA_LOAD_JOBS ready jobs is preloaded, with ACTA_LOAD_EXECUTORS concurrent executors and a 32-row claim batch configured.",
    Act = "The real batch-claim dispatch loop drains the backlog while throughput and latency percentiles are recorded.",
    Assert = "Every job in the backlog lands Succeeded exactly once."
)]
public abstract class StressLoadSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private const int ClaimBatch = 32;

    private const int EnqueueChunk = 1000;

    private static int JobCount => EnvInt("ACTA_LOAD_JOBS", 200);

    private static int Executors => EnvInt("ACTA_LOAD_EXECUTORS", 8);

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        services.AddSingleton<LoadLatencySink>();
        services.Configure<JobsOptions>(o =>
        {
            o.ClaimBatchSize = ClaimBatch;
            o.MaxConcurrentExecutors = Executors;
        });
    }

    [Fact(DisplayName = "Every enqueued job executes exactly once and the whole backlog drains to completion")]
    public async Task Backlog_drains_exactly_once_and_reports_latency()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobs = JobCount;
        var sink = Services.GetRequiredService<LoadLatencySink>();

        // Preload the whole backlog in batched bulk so the load window collapses; that keeps the
        // enqueue-stamp close to drain-start, so end-to-end latency reflects queue residence under
        // depth, not the time spent loading the queue.
        var enqueue = Stopwatch.StartNew();
        var batch = new List<JobEnqueueRequest>(Math.Min(EnqueueChunk, jobs));
        for (var i = 0; i < jobs; i++)
        {
            batch.Add(new JobEnqueueRequest(TestNamespace, "load-echo", JobPayload.Json(new LoadEcho(Stopwatch.GetTimestamp()))));
            if (batch.Count == EnqueueChunk || i == jobs - 1)
            {
                await Jobs.EnqueueBatchAsync(batch, ct);
                batch.Clear();
            }
        }
        enqueue.Stop();

        var drain = Stopwatch.StartNew();
        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loop = Runtime.RunLoopAsync(loopCts.Token);

        var deadline = TimeSpan.FromMinutes(5);
        while (sink.ElapsedTicks.Count < jobs && drain.Elapsed < deadline)
        {
            await Task.Delay(25, ct);
        }
        drain.Stop();

        await loopCts.CancelAsync();
        await loop;

        // The handler records exactly one sample per execution, so a count equal to the enqueued total
        // is the exactly-once + full-drain proof in one O(1) assertion, no per-job DB read needed.
        Assert.Equal(jobs, sink.ElapsedTicks.Count);

        Console.WriteLine($"[stress] {Fixture.GetType().Name} jobs={jobs} executors={Executors} claimBatch={ClaimBatch}");
        Console.WriteLine($"[stress] enqueue   {jobs / enqueue.Elapsed.TotalSeconds, 8:F0}/s  ({enqueue.Elapsed.TotalSeconds:F2}s)");
        Console.WriteLine($"[stress] complete  {jobs / drain.Elapsed.TotalSeconds, 8:F0}/s  ({drain.Elapsed.TotalSeconds:F2}s)");
        Console.WriteLine($"[stress] latency ms  {Percentiles([.. sink.ElapsedTicks])}  (enqueue to handler, under depth {jobs})");
    }

    [Fact(DisplayName = "Per-phase claim, start, and complete costs are reported (diagnostic)")]
    public async Task Per_phase_breakdown()
    {
        if (Environment.GetEnvironmentVariable("ACTA_PERF_PROBE") is null)
        {
            Assert.Skip("diagnostic: set ACTA_PERF_PROBE=1 to run");
        }

        var ct = TestContext.Current.CancellationToken;
        var n = JobCount;
        var dialect = Services.GetRequiredService<ISqlDialect>();
        var leaseTtl = Services.GetRequiredService<IOptions<JobsOptions>>().Value.LeaseTtlSeconds;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];

        var worker = await Db.From<JobWorker>().Where(w => w.NamespaceId == ns).SingleOrDefaultAsync(ct);
        var workerId = worker!.Id;

        var batch = new List<JobEnqueueRequest>(Math.Min(EnqueueChunk, n));
        for (var i = 0; i < n; i++)
        {
            batch.Add(new JobEnqueueRequest(TestNamespace, "load-echo", JobPayload.Json(new LoadEcho(0))));
            if (batch.Count == EnqueueChunk || i == n - 1)
            {
                await Jobs.EnqueueBatchAsync(batch, ct);
                batch.Clear();
            }
        }

        // Claim everything (timed), then single-threaded start+complete per job isolates per-op DB
        // cost from executor concurrency and poll-loop noise.
        var claimSw = Stopwatch.StartNew();
        var claimed = new List<ClaimedJob>(n);
        while (claimed.Count < n)
        {
            var got = await Services
                .GetRequiredService<IExecutionStore>()
                .ClaimBatchAsync(new ClaimRequest(ns, workerId, MaxBatch: 256), leaseTtl, ct);
            if (got.Jobs.Count == 0)
            {
                break;
            }

            claimed.AddRange(got.Jobs);
        }
        claimSw.Stop();

        // Read-only round-trip baseline (no commit fsync): whatever start/complete cost beyond this is
        // the write + durable-commit (fsync) cost.
        var nows = new long[Math.Min(claimed.Count, 500)];
        for (var i = 0; i < nows.Length; i++)
        {
            var t = Stopwatch.GetTimestamp();
            await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct);
            nows[i] = Stopwatch.GetTimestamp() - t;
        }

        var starts = new long[claimed.Count];
        var completes = new long[claimed.Count];
        for (var i = 0; i < claimed.Count; i++)
        {
            var c = claimed[i];
            var t0 = Stopwatch.GetTimestamp();
            await Services
                .GetRequiredService<IExecutionStore>()
                .StartExecutionAsync(c.JobId, workerId, c.ExecutionNumber, c.Version, leaseTtl, ct);
            var t1 = Stopwatch.GetTimestamp();
            await Services
                .GetRequiredService<IExecutionStore>()
                .CompleteExecutionAsync(
                    new CompleteExecutionRequest(
                        c.JobId,
                        workerId,
                        c.ExecutionNumber,
                        ExecutionOutcome.Succeeded,
                        0,
                        ReadOnlyMemory<byte>.Empty,
                        DurationMs: 0
                    ),
                    ct
                );
            var t2 = Stopwatch.GetTimestamp();
            starts[i] = t1 - t0;
            completes[i] = t2 - t1;
        }

        Console.WriteLine($"[probe] {Fixture.GetType().Name} n={claimed.Count}");
        Console.WriteLine($"[probe] claim/row ms  {1000.0 * claimSw.Elapsed.TotalSeconds / Math.Max(1, claimed.Count):F3}  (batched 256)");
        Console.WriteLine($"[probe] getnow   ms  {Percentiles(nows)}  (read-only round-trip, no fsync)");
        Console.WriteLine($"[probe] start    ms  {Percentiles(starts)}");
        Console.WriteLine($"[probe] complete ms  {Percentiles(completes)}");
    }

    private static string Percentiles(long[] ticks)
    {
        Array.Sort(ticks);
        double Ms(long t) => t * 1000.0 / Stopwatch.Frequency;
        double At(double q) => Ms(ticks[Math.Clamp((int)(q * ticks.Length), 0, ticks.Length - 1)]);
        return $"p50={At(0.50):F2} p95={At(0.95):F2} p99={At(0.99):F2} max={Ms(ticks[^1]):F2}";
    }

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;
}
