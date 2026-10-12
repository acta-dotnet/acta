using System.Diagnostics;
using Acta;

namespace Anvil.Bench;

/// <summary>
/// Batch-enqueue / fleet insert throughput: <c>Workers</c> concurrent producers each <c>EnqueueBatchAsync</c>
/// a share of the jobs into the one shared namespace, with a far horizon so nothing drains during the
/// measurement. <c>Workers=1</c> is the single-producer batch rate; <c>Workers=4/16</c> measures how many
/// jobs a fleet can insert under shared-table write contention (index leaf latches, identity/sequence, the
/// parent lock, tempdb TVP materialization on SQL Server). Isolates the batch path the per-call
/// <see cref="EnqueueScenario"/> never exercises and that throughput only touches in a sub-second,
/// drain-overlapped window, so a batch-routine regression surfaces as a gated jobs/sec drop.
/// </summary>
public sealed class EnqueueBatchScenario : IScenario
{
    // This scenario writes rows and never drains them, so it runs roughly two orders of magnitude
    // faster per job than throughput or drain. At the shared preset job count a cell finished in
    // about 0.4s, short enough that process warmup and OS scheduling dominated it: repeats of the
    // same commit spread 30-50%, which is wider than any regression the cell is meant to catch. It
    // therefore takes its own row count (BenchPreset.EnqueueBatchJobs, two orders of magnitude above
    // the shared count) so the measured window is seconds rather than milliseconds.
    //
    // Rates stay in the same units (enq/s over the aggregate producer wall clock), but they are NOT
    // comparable across the resizing: a baseline captured before it measured a different amount of
    // work, and the per-row cost changes with volume (page splits, index growth, WAL churn). Compare
    // enqueue-batch numbers only against baselines whose cell key carries the same job count.
    private const int IdleHorizonSeconds = 3600;

    public string Name => "enqueue-batch";

    public string Description => "Batch-enqueue fleet insert throughput (N producers, one namespace, no drain).";

    public async Task<CellMetrics> RunAsync(CellParams p, string schema, BenchConfig cfg, CancellationToken ct)
    {
        await using var host = await BenchHost.StartAsync(
            new BenchHostOptions
            {
                Provider = p.Provider,
                Schema = schema,
                Executors = p.Executors,
                SeedHistory = cfg.SeedHistory,
            },
            ct
        );

        // Fan out producer tasks, each batch-enqueuing its share concurrently into the shared namespace.
        // Aggregate wall-clock over the fleet is the fleet insert rate; a single worker uses one producer.
        var producers = Math.Max(1, p.Workers);
        var sw = Stopwatch.StartNew();
        await Task.WhenAll(
            Enumerable
                .Range(0, producers)
                .Select(i =>
                    Workload.EnqueueAsync(
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
        return new CellMetrics(
            EnqueueRatePerSec: Stats.RatePerSec(enqueued, sw.Elapsed.TotalSeconds),
            EndToEndRatePerSec: 0,
            DrainRatePerSec: 0,
            LatencyP50Ms: 0,
            LatencyP95Ms: 0,
            LatencyP99Ms: 0,
            LatencyMaxMs: 0,
            LatencyMeanMs: 0,
            EnqueueSeconds: sw.Elapsed.TotalSeconds,
            DrainSeconds: 0,
            JobsObserved: enqueued,
            Extra: new Dictionary<string, double> { ["producers"] = producers }
        );
    }
}
