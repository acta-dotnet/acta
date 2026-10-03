using System.Diagnostics;
using Acta;

namespace Anvil.Bench;

/// <summary>
/// Standard load-test shapes over the paced producer. constant = soak (fixed rate for a duration; reports
/// latency drift, last-decile p95 vs first-decile p95); ramp = capacity/breakpoint (step the rate up; max
/// sustainable = the highest step whose backlog still keeps up, queue-growth knee); spike = idle then a
/// single burst (reports peak queue depth + recover-to-baseline seconds). Reuses the audit-off bench-run
/// handler, so the headline measures framework overhead, not handler work.
/// </summary>
public sealed class LoadProfileScenario : IScenario
{
    public string Name => "loadprofile";

    public string Description => "Standard load shapes: soak (constant), capacity (ramp), spike.";

    public int ExpectedObserved(CellParams p) => 1;

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

        return cfg.LoadProfile.ToLowerInvariant() switch
        {
            "ramp" => await RampAsync(host.Jobs, host.Sink, p, cfg, ct),
            "spike" => await SpikeAsync(host.Jobs, host.Sink, p, cfg, ct),
            _ => await SoakAsync(host.Jobs, host.Sink, p, cfg, ct),
        };
    }

    // SOAK: constant rate for the whole duration; drift = last-decile p95 vs first-decile p95.
    private static async Task<CellMetrics> SoakAsync(IJobs jobs, BenchSink sink, CellParams p, BenchConfig cfg, CancellationToken ct)
    {
        var duration = TimeSpan.FromSeconds(cfg.DurationSec);
        var emitted = await Workload.ProduceConstantAsync(jobs, cfg.RatePerSec, duration, p.PayloadBytes, ct);
        await WaitForCountAsync(sink, emitted, Workload.DrainDeadline, ct);
        var ticks = LatencyTicks(sink);
        var (P50, P95, P99, Max, Mean) = Stats.Percentiles(ticks);
        var observed = sink.Samples.Count;
        return new CellMetrics(
            EnqueueRatePerSec: 0,
            EndToEndRatePerSec: Stats.RatePerSec(observed, duration.TotalSeconds),
            DrainRatePerSec: 0,
            LatencyP50Ms: P50,
            LatencyP95Ms: P95,
            LatencyP99Ms: P99,
            LatencyMaxMs: Max,
            LatencyMeanMs: Mean,
            EnqueueSeconds: 0,
            DrainSeconds: duration.TotalSeconds,
            JobsObserved: observed,
            Extra: new Dictionary<string, double>
            {
                ["latencyDriftPct"] = LatencyDriftPct(ticks),
                ["ratePerSec"] = cfg.RatePerSec,
                ["emitted"] = emitted,
            }
        );
    }

    // RAMP: step the rate up; a step "keeps up" if completions during it reach ~95% of what it produced.
    // Max sustainable = the last keeping-up step's rate; first step that falls behind marks saturation.
    private static async Task<CellMetrics> RampAsync(IJobs jobs, BenchSink sink, CellParams p, BenchConfig cfg, CancellationToken ct)
    {
        const int steps = 8;
        var stepWindow = TimeSpan.FromSeconds(Math.Max(3, cfg.DurationSec / steps));
        var maxSustained = 0;
        var totalEmitted = 0;
        for (var k = 1; k <= steps && !ct.IsCancellationRequested; k++)
        {
            var rate = cfg.RatePerSec * k / steps;
            var before = sink.Samples.Count;
            var produced = await Workload.ProduceConstantAsync(jobs, rate, stepWindow, p.PayloadBytes, ct);
            totalEmitted += produced;
            await Task.Delay(500, ct);
            var completed = sink.Samples.Count - before;
            if (completed >= produced * 0.95)
            {
                maxSustained = rate;
            }
            else
            {
                break;
            }
        }
        await WaitForCountAsync(sink, totalEmitted, Workload.DrainDeadline, ct);
        var (P50, P95, P99, Max, Mean) = Stats.Percentiles(LatencyTicks(sink));
        var observed = sink.Samples.Count;
        return new CellMetrics(
            EnqueueRatePerSec: 0,
            EndToEndRatePerSec: 0,
            DrainRatePerSec: 0,
            LatencyP50Ms: P50,
            LatencyP95Ms: P95,
            LatencyP99Ms: P99,
            LatencyMaxMs: Max,
            LatencyMeanMs: Mean,
            EnqueueSeconds: 0,
            DrainSeconds: 0,
            JobsObserved: observed,
            Extra: new Dictionary<string, double> { ["maxSustainableRatePerSec"] = maxSustained, ["emitted"] = totalEmitted }
        );
    }

    // SPIKE: one burst enqueued at once; recover = time until the burst has fully drained.
    private static async Task<CellMetrics> SpikeAsync(IJobs jobs, BenchSink sink, CellParams p, BenchConfig cfg, CancellationToken ct)
    {
        var burst = Math.Max(1000, cfg.RatePerSec * 5);
        await Workload.EnqueueAsync(jobs, burst, p.PayloadBytes, delaySeconds: null, ct);
        var recover = Stopwatch.StartNew();
        var drained = await WaitForCountAsync(sink, burst, Workload.DrainDeadline, ct);
        recover.Stop();
        var (P50, P95, P99, Max, Mean) = Stats.Percentiles(LatencyTicks(sink));
        var observed = sink.Samples.Count;
        return new CellMetrics(
            EnqueueRatePerSec: 0,
            EndToEndRatePerSec: 0,
            DrainRatePerSec: drained ? Stats.RatePerSec(observed, recover.Elapsed.TotalSeconds) : 0,
            LatencyP50Ms: P50,
            LatencyP95Ms: P95,
            LatencyP99Ms: P99,
            LatencyMaxMs: Max,
            LatencyMeanMs: Mean,
            EnqueueSeconds: 0,
            DrainSeconds: recover.Elapsed.TotalSeconds,
            JobsObserved: observed,
            Extra: new Dictionary<string, double>
            {
                ["peakQueueDepth"] = burst,
                ["recoverSeconds"] = drained ? recover.Elapsed.TotalSeconds : -1,
            }
        );
    }

    private static long[] LatencyTicks(BenchSink sink) => [.. sink.Samples.ToArray().Select(s => s.Entry - s.Enqueued).Where(t => t >= 0)];

    private static double LatencyDriftPct(long[] ticks)
    {
        if (ticks.Length < 20)
        {
            return 0;
        }
        var n = ticks.Length / 10;
        var first = Stats.Percentiles(ticks[..n]).P95;
        var last = Stats.Percentiles(ticks[^n..]).P95;
        return first > 0 ? (last - first) / first * 100.0 : 0;
    }

    private static async Task<bool> WaitForCountAsync(BenchSink sink, int target, TimeSpan deadline, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sink.Samples.Count < target && sw.Elapsed < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(50, ct);
        }
        return sink.Samples.Count >= target;
    }
}
