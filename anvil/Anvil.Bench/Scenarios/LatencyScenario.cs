using System.Diagnostics;
using Acta;

namespace Anvil.Bench;

/// <summary>
/// Per-job round-trip latency at concurrency one: enqueue a single job, let the running worker pick it
/// up, read its poll-free handler-entry latency from the sink, repeat. Captures the durable round-trip
/// cost the fsync model predicts, free of batching and queue depth.
/// </summary>
public sealed class LatencyScenario : IScenario
{
    public string Name => "latency";

    public string Description => "Single-job round-trip latency at concurrency 1 (p50/p95/p99 ms).";

    public int ExpectedObserved(CellParams p) => p.Iterations;

    public async Task<CellMetrics> RunAsync(CellParams p, string schema, BenchConfig cfg, CancellationToken ct)
    {
        await using var host = await BenchHost.StartAsync(
            new BenchHostOptions
            {
                Provider = p.Provider,
                Schema = schema,
                Executors = Math.Max(1, p.Executors),
                ClaimBatch = Math.Max(1, p.ClaimBatch),
                Profile = p.Profile,
                SeedHistory = cfg.SeedHistory,
            },
            ct
        );
        host.Sink.Expect(int.MaxValue);

        var pad = Workload.Pad(p.PayloadBytes);
        var deadline = Stopwatch.StartNew();
        var perJobDeadline = TimeSpan.FromSeconds(30);

        async Task RunOne()
        {
            var target = host.Sink.Samples.Count + 1;
            await host.Jobs.EnqueueAsync(
                new EnqueueJobRequest(
                    BenchHost.Namespace,
                    BenchHost.JobName,
                    BenchPayloads.Json(new BenchInput(Stopwatch.GetTimestamp(), pad))
                ),
                ct
            );
            var jobStart = Stopwatch.StartNew();
            while (host.Sink.Samples.Count < target && jobStart.Elapsed < perJobDeadline)
            {
                await Task.Delay(1, ct);
            }
        }

        for (var w = 0; w < p.Iterations / 10 + 1; w++)
        {
            await RunOne();
        }
        var cut = host.Sink.Samples.Count;

        for (var i = 0; i < p.Iterations; i++)
        {
            await RunOne();
        }

        var measured = host.Sink.Samples.ToArray().Skip(cut).Select(s => s.Entry - s.Enqueued).Where(t => t >= 0).ToArray();
        var (P50, P95, P99, Max, Mean) = Stats.Percentiles(measured);
        return new CellMetrics(
            EnqueueRatePerSec: 0,
            EndToEndRatePerSec: Stats.RatePerSec(measured.Length, deadline.Elapsed.TotalSeconds),
            DrainRatePerSec: 0,
            LatencyP50Ms: P50,
            LatencyP95Ms: P95,
            LatencyP99Ms: P99,
            LatencyMaxMs: Max,
            LatencyMeanMs: Mean,
            EnqueueSeconds: 0,
            DrainSeconds: deadline.Elapsed.TotalSeconds,
            JobsObserved: measured.Length
        );
    }
}
