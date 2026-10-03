using System.Diagnostics;
using Acta;

namespace Anvil.Bench;

/// <summary>
/// The headline scenario: enqueue and drain overlapped under one wall clock, exactly as
/// <c>host.StartAsync()</c> runs in production. Reports enqueue-phase rate, end-to-end rate, and
/// queue-residence latency.
/// </summary>
public sealed class ThroughputScenario : IScenario
{
    public string Name => "throughput";

    public string Description => "Enqueue + drain overlapped (measured end-to-end jobs/sec).";

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
        host.Sink.Expect(p.Jobs);

        var total = Stopwatch.StartNew();
        var enqueue = await Workload.EnqueueAsync(
            host.Jobs,
            p.Jobs,
            p.PayloadBytes,
            delaySeconds: null,
            ct,
            jobName: BenchHost.WorkloadJobName(cfg.AuditOn),
            workMs: cfg.WorkMs
        );

        await Workload.WaitForDrain(host.Sink, ct);
        total.Stop();

        var (P50, P95, P99, Max, Mean) = Workload.Latencies(host.Sink, s => s.Entry - s.Enqueued);
        return new CellMetrics(
            EnqueueRatePerSec: Stats.RatePerSec(p.Jobs, enqueue.TotalSeconds),
            EndToEndRatePerSec: Stats.RatePerSec(host.Sink.Samples.Count, total.Elapsed.TotalSeconds),
            DrainRatePerSec: 0,
            LatencyP50Ms: P50,
            LatencyP95Ms: P95,
            LatencyP99Ms: P99,
            LatencyMaxMs: Max,
            LatencyMeanMs: Mean,
            EnqueueSeconds: enqueue.TotalSeconds,
            DrainSeconds: total.Elapsed.TotalSeconds,
            JobsObserved: host.Sink.Samples.Count
        );
    }
}
