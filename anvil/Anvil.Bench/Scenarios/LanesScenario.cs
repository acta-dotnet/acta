using System.Diagnostics;
using Acta;

namespace Anvil.Bench;

/// <summary>
/// Lanes against a hot concurrency key, in the throughput shape: enqueue and drain overlapped under one
/// wall clock, no-op handlers, pickup read as enqueue to handler entry. Four cells share one job count
/// (see <see cref="BaselineSuite.LanesJobs"/>). <c>unlaned</c> is the control.
/// <c>lane-wide</c> spreads the jobs round-robin over 1000 lanes, which is what lanes cost the aggregate
/// rate at the normal executor count. <c>key-hot</c> puts every job under
/// one concurrency key with its implicit limit of 1, and <c>lane-deep</c> puts every job in one lane;
/// both run one job at a time, so they compare the key's slot handoff with the lane's promotion handoff.
/// </summary>
public sealed class LanesScenario : IScenario
{
    public const string Unlaned = "unlaned";
    public const string LaneWide = "lane-wide";
    public const string KeyHot = "key-hot";
    public const string LaneDeep = "lane-deep";

    /// <summary>The cells in report order: the control first, then the shapes that serialize more.</summary>
    public static readonly string[] Variants = [Unlaned, LaneWide, KeyHot, LaneDeep];

    private const int WideLanes = 1000;
    private const string HotKey = "bench-hot";
    private const string DeepLane = "bench-deep";

    public string Name => "lanes";

    public string Description => "Unlaned, 1000 lanes, one hot concurrency key, and one deep lane (end-to-end jobs/s, pickup p50/p99).";

    public async Task<CellMetrics> RunAsync(CellParams p, string schema, BenchConfig cfg, CancellationToken ct)
    {
        var (concurrencyKey, laneOf) = p.Variant switch
        {
            Unlaned => ((string?)null, (Func<int, string?>?)null),
            LaneWide => (null, i => $"bench-lane-{i % WideLanes}"),
            KeyHot => (HotKey, null),
            LaneDeep => (null, _ => DeepLane),
            _ => throw new ArgumentException($"Unknown lanes cell '{p.Variant}' (expected {string.Join('|', Variants)}).", nameof(p)),
        };

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
            concurrencyKey: concurrencyKey,
            laneOf: laneOf
        );

        await Workload.WaitForDrain(host.Sink, ct);
        total.Stop();

        var (P50, P95, P99, Max, Mean) = Workload.Latencies(host.Sink, s => s.Entry - s.Enqueued);
        var lanes = p.Variant switch
        {
            LaneWide => WideLanes,
            LaneDeep => 1,
            _ => 0,
        };
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
            JobsObserved: host.Sink.Samples.Count,
            Extra: new Dictionary<string, double> { ["lanes"] = lanes }
        );
    }
}
