using System.Diagnostics;
using Acta;

namespace Anvil.Bench;

/// <summary>
/// Single-call enqueue / fleet insert throughput: <c>Workers</c> concurrent producers each enqueue a share
/// of the jobs one call at a time (the per-request path, <c>EnqueueAsync</c>) into the one shared namespace,
/// with a far horizon so nothing drains. <c>Workers=1</c> is the single-producer per-call rate + latency;
/// <c>Workers=4/16</c> measures how many jobs a fleet of single-call producers can insert under write
/// contention. Reports aggregate enqueue/sec and per-call latency percentiles across the fleet.
/// </summary>
public sealed class EnqueueScenario : IScenario
{
    private const int IdleHorizonSeconds = 3600;

    public string Name => "enqueue";

    public string Description => "Single-call enqueue fleet insert throughput + per-call latency (N producers, no drain).";

    public async Task<CellMetrics> RunAsync(CellParams p, string schema, BenchConfig cfg, CancellationToken ct)
    {
        await using var host = await BenchHost.StartAsync(
            new BenchHostOptions
            {
                Provider = p.Provider,
                Schema = schema,
                Executors = p.Executors,
                ClaimBatch = p.ClaimBatch,
                Profile = p.Profile,
                SeedHistory = cfg.SeedHistory,
            },
            ct
        );

        // Fan out Workers producers, each looping single-call enqueues concurrently into the shared
        // namespace; aggregate wall-clock is the fleet rate, per-call ticks pooled for the latency spread.
        var producers = Math.Max(1, p.Workers);
        var sw = Stopwatch.StartNew();
        var results = await Task.WhenAll(
            Enumerable
                .Range(0, producers)
                .Select(i =>
                    Workload.EnqueueEachAsync(
                        host.Jobs,
                        p.Jobs / producers + (i < p.Jobs % producers ? 1 : 0),
                        p.PayloadBytes,
                        IdleHorizonSeconds,
                        ct
                    )
                )
        );
        sw.Stop();

        var enqueued = p.Jobs;
        var (P50, P95, P99, Max, Mean) = Stats.Percentiles([.. results.SelectMany(r => r.Ticks)]);
        return new CellMetrics(
            EnqueueRatePerSec: Stats.RatePerSec(enqueued, sw.Elapsed.TotalSeconds),
            EndToEndRatePerSec: 0,
            DrainRatePerSec: 0,
            LatencyP50Ms: P50,
            LatencyP95Ms: P95,
            LatencyP99Ms: P99,
            LatencyMaxMs: Max,
            LatencyMeanMs: Mean,
            EnqueueSeconds: sw.Elapsed.TotalSeconds,
            DrainSeconds: 0,
            JobsObserved: enqueued,
            Extra: new Dictionary<string, double> { ["producers"] = producers }
        );
    }
}
