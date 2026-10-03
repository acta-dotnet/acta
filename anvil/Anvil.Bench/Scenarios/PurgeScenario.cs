using System.Collections.Concurrent;
using System.Diagnostics;
using Acta;

namespace Anvil.Bench;

/// <summary>
/// Purge cost and lock signal on a large expired set: drain an audit-on workload to produce events, set
/// the retention window to zero so all of them qualify, then drive <c>sys.retention</c> to
/// completion while a light enqueue probe runs concurrently. Reports purge duration, rows/sec, and the
/// probe's p95 (the lock/contention signal).
/// </summary>
public sealed class PurgeScenario : IScenario
{
    private const int DefaultSeedJobs = 200_000;
    private const int IdleHorizonSeconds = 36_000;

    public string Name => "purge";

    public string Description => "Purge a large expired event set: duration, rows/sec, writer-contention p95.";

    public int ExpectedObserved(CellParams p) => p.Rows > 0 ? p.Rows : DefaultSeedJobs;

    public async Task<CellMetrics> RunAsync(CellParams p, string schema, BenchConfig cfg, CancellationToken ct)
    {
        var seedJobs = p.Rows > 0 ? p.Rows : DefaultSeedJobs;
        var opt = new BenchHostOptions
        {
            Provider = p.Provider,
            Schema = schema,
            Executors = p.Executors,
            ClaimBatch = p.ClaimBatch,
            RegisterSystemJobs = true,
            // Seeded events land inside this cell's expired set by design: the sweep it measures is
            // the one that deletes them, which is the point of purging against a populated ledger.
            SeedHistory = cfg.SeedHistory,
            // Retention must be >= 1 day; we age the events past it below rather than zeroing the window.
            JobEventsRetentionDays = 1,
        };
        await using var host = await BenchHost.StartAsync(opt, ct);

        // Seed: drain an audit-on workload so every execution writes events.
        host.Sink.Expect(seedJobs);
        var seed = Stopwatch.StartNew();
        await Workload.EnqueueAsync(host.Jobs, seedJobs, 0, delaySeconds: null, ct, jobName: BenchHost.AuditJobName);
        await Workload.WaitForDrain(host.Sink, ct);
        seed.Stop();

        // Backdate every event past the 1-day retention window so the purge sweep deletes them all.
        await host.AgeAllEventsAsync(days: 2, ct);
        var events = await host.CountExpiredEventsAsync(olderThanDays: 1, ct);

        // Concurrent writer-contention probe: future-dated enqueues that never execute, one every ~50ms.
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var probeTicks = new List<long>();
        var probe = Task.Run(
            async () =>
            {
                while (!probeCts.IsCancellationRequested)
                {
                    try
                    {
                        var t = Stopwatch.GetTimestamp();
                        await host.Jobs.EnqueueAsync(
                            new JobEnqueueRequest(
                                BenchHost.Namespace,
                                BenchHost.JobName,
                                BenchPayloads.Json(new BenchInput(t)),
                                DelaySeconds: IdleHorizonSeconds
                            ),
                            probeCts.Token
                        );
                        lock (probeTicks)
                        {
                            probeTicks.Add(Stopwatch.GetTimestamp() - t);
                        }
                        await Task.Delay(50, probeCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            },
            probeCts.Token
        );

        // Drive the purge to completion.
        var purge = Stopwatch.StartNew();
        var budget = TimeSpan.FromMinutes(10);
        long remaining = events;
        while (remaining > 0 && purge.Elapsed < budget)
        {
            await host.TriggerSlotAsync(BenchHost.RetentionJobName, ct);
            remaining = await host.CountExpiredEventsAsync(olderThanDays: 1, ct);
        }
        purge.Stop();

        await probeCts.CancelAsync();
        try
        {
            await probe;
        }
        catch (OperationCanceledException) { }

        long[] probeArr;
        lock (probeTicks)
        {
            probeArr = [.. probeTicks];
        }
        var (P50, P95, P99, Max, Mean) = Stats.Percentiles(probeArr);
        var purgedRows = events - Math.Max(0, remaining);
        var rowsPerSec = purge.Elapsed.TotalSeconds > 0 ? purgedRows / purge.Elapsed.TotalSeconds : 0;

        return new CellMetrics(
            EnqueueRatePerSec: 0,
            EndToEndRatePerSec: 0,
            DrainRatePerSec: 0,
            LatencyP50Ms: P50,
            LatencyP95Ms: P95,
            LatencyP99Ms: P99,
            LatencyMaxMs: Max,
            LatencyMeanMs: Mean,
            EnqueueSeconds: seed.Elapsed.TotalSeconds,
            DrainSeconds: purge.Elapsed.TotalSeconds,
            JobsObserved: host.Sink.Samples.Count,
            Extra: new Dictionary<string, double>
            {
                ["purgeSeconds"] = purge.Elapsed.TotalSeconds,
                ["purgeRows"] = purgedRows,
                ["purgeRowsPerSec"] = rowsPerSec,
                ["contendedEnqueueP95Ms"] = P95,
                ["seedSeconds"] = seed.Elapsed.TotalSeconds,
                ["remainingRows"] = remaining,
            }
        );
    }
}
