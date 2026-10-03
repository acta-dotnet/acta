using System.Diagnostics;
using Acta;

namespace Anvil.Bench;

/// <summary>
/// A due backlog drained beside rows the claim can never take right now, in the drain shape: the due
/// jobs wait behind a horizon, and the timed window is pure drain from the instant they come due, with
/// pickup read from that instant to handler entry. <c>none</c> drains the due Normal jobs alone and is the
/// control. <c>parked</c> first parks <see cref="ParkedJobs"/> jobs on an unbounded wait (Suspended, no due
/// instant) in the same priority band as the due Normal jobs. <c>delayed-high</c> first enqueues
/// <see cref="DelayedHighJobs"/> High jobs due tomorrow and then drains due Bulk jobs, so every higher band
/// ahead of the due one holds only rows that are not due. No background is timed.
/// </summary>
public sealed class ClaimSkewScenario : IScenario
{
    public const string None = "none";
    public const string Parked = "parked";
    public const string DelayedHigh = "delayed-high";

    /// <summary>The cells in report order: the control first.</summary>
    public static readonly string[] Variants = [None, Parked, DelayedHigh];

    /// <summary>The due jobs each cell drains, on every preset: the cell measures the background, not the backlog.</summary>
    public const int DueJobs = 10_000;

    public const int ParkedJobs = 50_000;
    public const int DelayedHighJobs = 200_000;

    // Far enough out that the parked jobs cannot come due between their enqueue and the update that parks them.
    private const int ParkingDelaySeconds = 36_000;

    private const int OneDaySeconds = 86_400;

    public string Name => "claim-skew";

    public string Description =>
        "Due jobs drained alone, beside 50k parked waits, or beside 200k higher-priority delayed jobs (drain jobs/s, pickup p50/p99).";

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

        var (background, duePriority) = p.Variant switch
        {
            None => (0, (JobPriorityCode?)null),
            Parked => (ParkedJobs, null),
            DelayedHigh => (DelayedHighJobs, JobPriorityCode.Bulk),
            _ => throw new ArgumentException($"Unknown claim-skew cell '{p.Variant}' (expected {string.Join('|', Variants)}).", nameof(p)),
        };
        if (p.Variant == Parked)
        {
            var afterJobId = await ProviderConn.MaxJobIdAsync(p.Provider, schema, ct);
            await Workload.EnqueueAsync(host.Jobs, ParkedJobs, payloadBytes: 0, ParkingDelaySeconds, ct);
            await ProviderConn.ParkAboveAsync(p.Provider, schema, afterJobId, ct);
        }
        else if (p.Variant == DelayedHigh)
        {
            await Workload.EnqueueAsync(host.Jobs, DelayedHighJobs, payloadBytes: 0, OneDaySeconds, ct, priority: JobPriorityCode.High);
        }

        host.Sink.Expect(p.Jobs);
        var (enqueue, releaseStamp) = await Workload.PreloadBehindHorizonAsync(
            host.Jobs,
            p.Jobs,
            p.PayloadBytes,
            ct,
            priority: duePriority
        );

        var drain = Stopwatch.StartNew();
        await Workload.WaitForDrain(host.Sink, ct);
        drain.Stop();

        var (P50, P95, P99, Max, Mean) = Workload.Latencies(host.Sink, s => s.Entry - releaseStamp);
        return new CellMetrics(
            EnqueueRatePerSec: Stats.RatePerSec(p.Jobs, enqueue.TotalSeconds),
            EndToEndRatePerSec: 0,
            DrainRatePerSec: Stats.RatePerSec(host.Sink.Samples.Count, drain.Elapsed.TotalSeconds),
            LatencyP50Ms: P50,
            LatencyP95Ms: P95,
            LatencyP99Ms: P99,
            LatencyMaxMs: Max,
            LatencyMeanMs: Mean,
            EnqueueSeconds: enqueue.TotalSeconds,
            DrainSeconds: drain.Elapsed.TotalSeconds,
            JobsObserved: host.Sink.Samples.Count,
            Extra: new Dictionary<string, double> { ["backgroundJobs"] = background }
        );
    }
}
