using System.Diagnostics;
using Acta;

namespace Anvil.Bench;

/// <summary>
/// Per-definition rate limiting under load. A metered backlog is preloaded behind a horizon so every
/// job becomes due at once, the definition's meter is retuned to the cell's rate through the operator
/// override, and after the drain the cell reads back from <c>events</c> what the meter actually did.
/// Reports the admitted rate over the admission span, the busiest one-second window, how far the
/// busiest 1s/5s/10s windows sit above the published R*(T+W)+B ceiling, and the re-arms each job paid for
/// its turn. A cell with <c>SideJobs</c> releases an unmetered <c>bench-run</c> backlog at the same
/// instant, so what a busy meter costs the jobs beside it reads as sideP50Ms / sideP99Ms.
/// </summary>
public sealed class RateScenario : IScenario
{
    // The windows the rate contract is published over: at most R*(T+W) + B admissions in any T seconds,
    // W being how long a booked turn stays valid (one second, or one interval when longer). Reported
    // as an excess above that, never asserted: this is a measurement.
    private static readonly double[] ContractWindows = [1, 5, 10];

    // Workers pick a changed rate up on their definition-policy reload, which runs on the safety poll
    // interval (one second by default), so the cell waits that out before it enqueues anything.
    private static readonly TimeSpan PolicyReloadGrace = TimeSpan.FromSeconds(3);

    public string Name => "rate";

    public string Description => "Per-definition rate limiting: admitted jobs/s, window ceilings, and re-arms per job.";

    public int ExpectedObserved(CellParams p) => p.Jobs + p.SideJobs;

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
        };

        if (p.Workers > 1)
        {
            await using var cluster = await BenchCluster.StartAsync(template, p.Workers, ct);
            return await MeasureAsync(cluster.Jobs, cluster.Queries, cluster.Sink, p, schema, p.Workers, ct);
        }

        await using var host = await BenchHost.StartAsync(template, ct);
        return await MeasureAsync(host.Jobs, host.Queries, host.Sink, p, schema, 1, ct);
    }

    private static async Task<CellMetrics> MeasureAsync(
        IJobs jobs,
        IActaOperations operations,
        BenchSink sink,
        CellParams p,
        string schema,
        int workers,
        CancellationToken ct
    )
    {
        var rateText = p.Rate ?? BenchHost.DeclaredRate;
        var rate = RateLimitSpec.Parse(rateText);
        await ApplyRateAsync(operations, rateText, ct);
        await Task.Delay(PolicyReloadGrace, ct);
        sink.Expect(p.Jobs + p.SideJobs);

        var (enqueue, releaseStamp) = await Workload.PreloadBehindHorizonAsync(
            jobs,
            p.Jobs,
            p.PayloadBytes,
            ct,
            jobName: BenchHost.RateJobName,
            alongsideCount: p.SideJobs,
            alongsideJobName: BenchHost.JobName
        );

        var drain = Stopwatch.StartNew();
        await Workload.WaitForDrain(sink, ct);
        drain.Stop();

        var admissions = await ProviderConn.ReadRateAdmissionsAsync(p.Provider, schema, ct);
        await ApplyRateAsync(operations, null, ct);

        // The meter emits one admission per whole-millisecond interval, so the interval is the rate
        // the contract is checked against: "600/m" is 10/s with a burst of 10, not 600 at once.
        var perSecond = 1000.0 / rate.IntervalMilliseconds;
        // How long a booked turn stays valid past its instant: a second, or the interval when longer.
        var validitySeconds = Math.Max(1.0, rate.IntervalMilliseconds / 1000.0);
        var instants = admissions.AdmittedAtUtc;
        var span = instants.Count > 1 ? (instants[^1] - instants[0]).TotalSeconds : 0;
        var admittedPerSec = Stats.RatePerSec(instants.Count, span);
        var metered = Math.Max(1L, admissions.MeteredJobs);

        var extra = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["workers"] = workers,
            ["declaredPerSec"] = perSecond,
            ["burst"] = rate.Burst,
            ["intervalMs"] = rate.IntervalMilliseconds,
            ["admitted"] = instants.Count,
            ["admittedPerSec"] = admittedPerSec,
            ["admittedSpanSec"] = span,
            ["attemptsPerJob"] = admissions.Attempts / (double)metered,
            ["rearmsPerJob"] = admissions.Rearms / (double)metered,
            // Exactly one reserve call per attempt: the meter is asked once per execution start.
            ["reserveCallsPerSec"] = Stats.RatePerSec((int)Math.Min(admissions.Attempts, int.MaxValue), span),
            ["maxStartsIn1s"] = MaxInWindow(instants, 1),
        };
        foreach (var window in ContractWindows)
        {
            extra[$"contractExcess{window:F0}s"] = MaxInWindow(instants, window) - ((perSecond * (window + validitySeconds)) + rate.Burst);
        }

        // The headline latency is how long a metered job waited for its turn, which the meter sets;
        // the unmetered jobs beside it are the ones whose latency says what the meter cost them.
        var (P50, P95, P99, Max, Mean) = Workload.Latencies(sink.MeteredSamples, s => s.Entry - releaseStamp);
        if (p.SideJobs > 0)
        {
            var side = Workload.Latencies(sink.Samples, s => s.Entry - releaseStamp);
            extra["sideObserved"] = sink.Samples.Count;
            extra["sideP50Ms"] = side.P50;
            extra["sideP99Ms"] = side.P99;
        }

        return new CellMetrics(
            EnqueueRatePerSec: Stats.RatePerSec(p.Jobs + p.SideJobs, enqueue.TotalSeconds),
            EndToEndRatePerSec: admittedPerSec,
            DrainRatePerSec: 0,
            LatencyP50Ms: P50,
            LatencyP95Ms: P95,
            LatencyP99Ms: P99,
            LatencyMaxMs: Max,
            LatencyMeanMs: Mean,
            EnqueueSeconds: enqueue.TotalSeconds,
            DrainSeconds: drain.Elapsed.TotalSeconds,
            JobsObserved: sink.MeteredSamples.Count + sink.Samples.Count,
            Extra: extra
        );
    }

    /// <summary>
    /// Points the bench meter at <paramref name="rate"/>, or clears the override when it is null so the
    /// definition falls back to what it declares. The override is version-guarded, so the current
    /// definition is read first; one definition therefore covers every rate in the sweep.
    /// </summary>
    private static async Task ApplyRateAsync(IActaOperations operations, string? rate, CancellationToken ct)
    {
        var definition =
            await operations.Definitions.GetAsync(BenchHost.Namespace, BenchHost.RateJobName, ct)
            ?? throw new InvalidOperationException($"Definition \"{BenchHost.RateJobName}\" is not registered in this schema.");
        var outcome = await operations.Definitions.UpdateOverridesAsync(
            BenchHost.Namespace,
            BenchHost.RateJobName,
            definition.Version,
            new JobDefinitionPolicyOverrides(RateLimit: rate),
            ct: ct
        );
        if (outcome.Action != ControlAction.Applied)
        {
            throw new InvalidOperationException($"Setting the bench rate override to \"{rate ?? "none"}\" returned {outcome.Action}.");
        }
    }

    /// <summary>
    /// The most admissions any window of <paramref name="seconds"/> holds, walked once over the sorted
    /// instants: the sliding-window count the R*(T+W) + B ceiling is stated over.
    /// </summary>
    private static int MaxInWindow(IReadOnlyList<DateTime> instants, double seconds)
    {
        var window = TimeSpan.FromSeconds(seconds);
        var max = 0;
        var first = 0;
        for (var i = 0; i < instants.Count; i++)
        {
            while (instants[i] - instants[first] > window)
            {
                first++;
            }
            max = Math.Max(max, i - first + 1);
        }
        return max;
    }
}
