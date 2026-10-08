using System.Diagnostics;
using Acta;

namespace Anvil.Bench;

/// <summary>
/// Wakeup-fallback latency: single-job pickup latency with a raised poll floor, measured under in-process
/// wakeup (instant) and a no-op wakeup (poll-only), and optionally Redis. The headline latency is the
/// no-op (fallback) distribution; per-mode pickup p50/p95 land in Extra.
/// </summary>
public sealed class WakeupScenario : IScenario
{
    private static readonly TimeSpan DefaultPoll = TimeSpan.FromSeconds(1);

    public string Name => "wakeup";

    public string Description => "Pickup latency with wakeup on vs off (poll-fallback baseline).";

    public int ExpectedObserved(CellParams p) => 1;

    public async Task<CellMetrics> RunAsync(CellParams p, string schema, BenchConfig cfg, CancellationToken ct)
    {
        var poll = cfg.SafetyPollInterval ?? DefaultPoll;
        var samples = Math.Max(5, Math.Min(p.Iterations, 30));

        var (P50, P95, P99, Max, Mean) = await MeasureAsync(
            p,
            schema,
            poll,
            BenchWakeupMode.InProcess,
            null,
            samples,
            resetSchema: true,
            cfg.SeedHistory,
            ct
        );
        var noOp = await MeasureAsync(p, schema, poll, BenchWakeupMode.NoOp, null, samples, resetSchema: true, cfg.SeedHistory, ct);

        var extra = new Dictionary<string, double>
        {
            ["pickupInProcP50Ms"] = P50,
            ["pickupInProcP95Ms"] = P95,
            ["pickupNoOpP50Ms"] = noOp.P50,
            ["pickupNoOpP95Ms"] = noOp.P95,
            ["pollMs"] = poll.TotalMilliseconds,
        };
        if (cfg.RedisConfig is not null)
        {
            var redis = await MeasureAsync(
                p,
                schema,
                poll,
                BenchWakeupMode.Redis,
                cfg.RedisConfig,
                samples,
                resetSchema: true,
                cfg.SeedHistory,
                ct
            );
            extra["pickupRedisP50Ms"] = redis.P50;
            extra["pickupRedisP95Ms"] = redis.P95;
        }

        // Headline latency = the no-op fallback distribution.
        return new CellMetrics(
            EnqueueRatePerSec: 0,
            EndToEndRatePerSec: 0,
            DrainRatePerSec: 0,
            LatencyP50Ms: noOp.P50,
            LatencyP95Ms: noOp.P95,
            LatencyP99Ms: noOp.P99,
            LatencyMaxMs: noOp.Max,
            LatencyMeanMs: noOp.Mean,
            EnqueueSeconds: 0,
            DrainSeconds: 0,
            JobsObserved: samples,
            Extra: extra
        );
    }

    private static async Task<(double P50, double P95, double P99, double Max, double Mean)> MeasureAsync(
        CellParams p,
        string schema,
        TimeSpan poll,
        BenchWakeupMode mode,
        string? redis,
        int samples,
        bool resetSchema,
        int seedHistory,
        CancellationToken ct
    )
    {
        var opt = new BenchHostOptions
        {
            Provider = p.Provider,
            Schema = schema,
            Executors = 1,
            ClaimBatch = 1,
            SafetyPollInterval = poll,
            Wakeup = mode,
            RedisConfig = redis,
            ResetSchema = resetSchema,
            SeedHistory = seedHistory,
        };
        await using var host = await BenchHost.StartAsync(opt, ct);
        host.Sink.Expect(int.MaxValue);

        var perJobDeadline = poll + TimeSpan.FromSeconds(5);
        for (var i = 0; i < samples; i++)
        {
            var target = host.Sink.Samples.Count + 1;
            await host.Jobs.EnqueueAsync(
                new EnqueueJobRequest(BenchHost.Namespace, BenchHost.JobName, BenchPayloads.Json(new BenchInput(Stopwatch.GetTimestamp()))),
                ct
            );
            var jobStart = Stopwatch.StartNew();
            while (host.Sink.Samples.Count < target && jobStart.Elapsed < perJobDeadline)
            {
                await Task.Delay(1, ct);
            }
        }

        var ticks = host.Sink.Samples.ToArray().Select(s => s.Entry - s.Enqueued).Where(t => t >= 0).ToArray();
        return Stats.Percentiles(ticks);
    }
}
