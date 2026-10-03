using System.Diagnostics;
using Acta;

namespace Anvil.Bench;

/// <summary>
/// Worker-kill recovery: two workers, a short lease, one blocking probe job. The worker that picks it up
/// is killed mid-execution; the job's lease lapses and maintenance reclaims it onto the survivor, which
/// re-runs it. The measured time from kill to re-completion is the recovery latency.
/// </summary>
public sealed class RecoveryScenario : IScenario
{
    public string Name => "recovery";

    public string Description => "Kill a worker mid-job; time until its lease is stolen and the job re-runs.";

    public int ExpectedObserved(CellParams p) => 1;

    public async Task<CellMetrics> RunAsync(CellParams p, string schema, BenchConfig cfg, CancellationToken ct)
    {
        var template = new BenchHostOptions
        {
            Provider = p.Provider,
            Schema = schema,
            Executors = Math.Max(1, p.Executors),
            ClaimBatch = p.ClaimBatch,
            LeaseTtlSeconds = cfg.LeaseTtlSeconds,
            RegisterSystemJobs = true,
            SeedHistory = cfg.SeedHistory,
        };
        await using var cluster = await BenchCluster.StartAsync(template, workers: 2, ct);
        cluster.Sink.Expect(1);

        await cluster.Jobs.EnqueueAsync(
            new JobEnqueueRequest(
                BenchHost.Namespace,
                BenchHost.BlockJobName,
                BenchPayloads.Json(new BenchInput(Stopwatch.GetTimestamp()))
            ),
            ct
        );

        int enteredHost;
        try
        {
            enteredHost = await cluster.Recovery.Entered.WaitAsync(TimeSpan.FromSeconds(30), ct);
        }
        catch (TimeoutException)
        {
            return Failed("no worker claimed the probe job within 30s");
        }

        var killStamp = Stopwatch.GetTimestamp();
        cluster.Kill(enteredHost);

        // Force the reclaim sweep: the survivor runs each enqueued maintenance pass, which reclaims the
        // stuck job once its lease has lapsed.
        var deadline = Stopwatch.StartNew();
        var budget = TimeSpan.FromSeconds(cfg.LeaseTtlSeconds + 60);
        while (cluster.Sink.Samples.IsEmpty && deadline.Elapsed < budget)
        {
            await cluster.Jobs.EnqueueAsync(new JobEnqueueRequest(BenchHost.Namespace, BenchHost.RecoveryJobName), ct);
            try
            {
                await cluster.Sink.Completed.WaitAsync(TimeSpan.FromSeconds(1), ct);
            }
            catch (TimeoutException) { }
        }

        if (!cluster.Sink.Samples.TryPeek(out var sample))
        {
            return Failed($"job not recovered within {budget.TotalSeconds:F0}s");
        }

        var recoveryMs = Stats.Ms(sample.Entry - killStamp);
        return new CellMetrics(
            EnqueueRatePerSec: 0,
            EndToEndRatePerSec: 0,
            DrainRatePerSec: 0,
            LatencyP50Ms: recoveryMs,
            LatencyP95Ms: recoveryMs,
            LatencyP99Ms: recoveryMs,
            LatencyMaxMs: recoveryMs,
            LatencyMeanMs: recoveryMs,
            EnqueueSeconds: 0,
            DrainSeconds: recoveryMs / 1000.0,
            JobsObserved: cluster.Sink.Samples.Count,
            Extra: new Dictionary<string, double> { ["recoveryMs"] = recoveryMs, ["leaseTtlSeconds"] = cfg.LeaseTtlSeconds }
        );

        static CellMetrics Failed(string _) => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }
}
