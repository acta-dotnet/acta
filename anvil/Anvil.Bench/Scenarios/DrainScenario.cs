using System.Diagnostics;
using Acta;

namespace Anvil.Bench;

/// <summary>
/// Claim + execute + complete only, write cost removed. Parameterized by Workers (N>1 runs N in-process
/// workers draining one preloaded backlog - the claim-contention sweep - spread over Namespaces, as a
/// deployment runs several namespaces of a few workers each); SharedKey makes every job
/// carry one concurrency key so the lock serializes them (the contention/fairness probe). The whole backlog
/// is enqueued behind a future horizon (nothing Ready), then the horizon passes and a fully-preloaded
/// queue drains; the timed window is pure drain. Reports drain jobs/sec, per-job overhead, worker count,
/// and (when shared-key) fairness spread (p99/p50 of queue residence).
/// </summary>
public sealed class DrainScenario : IScenario
{
    private const string SharedKey = "bench-shared";

    public string Name => "drain";

    public string Description => "Claim + execute + complete only (drain jobs/sec; worker sweep and optional shared-key contention).";

    public async Task<CellMetrics> RunAsync(CellParams p, string schema, BenchConfig cfg, CancellationToken ct)
    {
        var template = new BenchHostOptions
        {
            Provider = p.Provider,
            Schema = schema,
            Executors = p.Executors,
            ClaimBatch = p.ClaimBatch,
            Profile = p.Profile,
            SeedHistory = cfg.SeedHistory,
            // Fast heartbeat so lease extension runs inside short benchmark cells.
            LeaseTtlSeconds = 30,
        };

        if (p.Workers > 1)
        {
            await using var cluster = await BenchCluster.StartAsync(template, p.Workers, ct, p.Namespaces);
            return await DrainCoreAsync(cluster.Jobs, cluster.Sink, p, cfg, p.Workers, ct);
        }

        await using var host = await BenchHost.StartAsync(template, ct);
        return await DrainCoreAsync(host.Jobs, host.Sink, p, cfg, 1, ct);
    }

    private static async Task<CellMetrics> DrainCoreAsync(
        IJobs jobs,
        BenchSink sink,
        CellParams p,
        BenchConfig cfg,
        int workers,
        CancellationToken ct
    )
    {
        sink.Expect(p.Jobs);

        var (enqueue, releaseStamp) = await Workload.PreloadBehindHorizonAsync(
            jobs,
            p.Jobs,
            p.PayloadBytes,
            ct,
            concurrencyKey: cfg.SharedKey ? SharedKey : null,
            jobName: BenchHost.WorkloadJobName(cfg.AuditOn),
            workMs: cfg.WorkMs,
            namespaces: p.Namespaces
        );

        var drain = Stopwatch.StartNew();
        await Workload.WaitForDrain(sink, ct);
        drain.Stop();

        var (P50, P95, P99, Max, Mean) = Workload.Latencies(sink, s => s.Entry - releaseStamp);
        var overheadUs = !sink.Samples.IsEmpty ? drain.Elapsed.TotalSeconds / sink.Samples.Count * 1e6 : 0;
        var extra = new Dictionary<string, double>
        {
            ["overheadUsPerJob"] = overheadUs,
            ["workers"] = workers,
            ["namespaces"] = p.Namespaces,
        };
        if (cfg.SharedKey)
        {
            extra["fairnessSpread"] = P50 > 0 ? P99 / P50 : 0;
        }

        return new CellMetrics(
            EnqueueRatePerSec: Stats.RatePerSec(p.Jobs, enqueue.TotalSeconds),
            EndToEndRatePerSec: 0,
            DrainRatePerSec: Stats.RatePerSec(sink.Samples.Count, drain.Elapsed.TotalSeconds),
            LatencyP50Ms: P50,
            LatencyP95Ms: P95,
            LatencyP99Ms: P99,
            LatencyMaxMs: Max,
            LatencyMeanMs: Mean,
            EnqueueSeconds: enqueue.TotalSeconds,
            DrainSeconds: drain.Elapsed.TotalSeconds,
            JobsObserved: sink.Samples.Count,
            Extra: extra
        );
    }
}
