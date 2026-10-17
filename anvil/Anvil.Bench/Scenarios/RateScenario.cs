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

    // A worker re-reads definition policy on its heartbeat, right after the lease renewal that stamps the
    // heartbeat, so a heartbeat stamped after the override is followed within moments by a reload that sees it.
    private static readonly TimeSpan PolicyReloadGrace = TimeSpan.FromSeconds(1);

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
        await WaitForPolicyReloadAsync(operations, ct);
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
        // The meter admits an attempt just before its handler starts, so each metered handler's entry stamp, on the
        // one monotonic clock every in-process host shares, is its admission instant. A finished event is stamped when
        // its completion batch commits, which can trail the handler by the batch's wait, so events count, never time.
        var instants = sink.MeteredSamples.Select(static s => DateTime.UnixEpoch + Stopwatch.GetElapsedTime(0, s.Entry)).Order().ToList();
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
            new DefinitionPolicyOverrides(RateLimit: rate),
            ct: ct
        );
        if (outcome.Action != ControlAction.Applied)
        {
            throw new InvalidOperationException($"Setting the bench rate override to \"{rate ?? "none"}\" returned {outcome.Action}.");
        }
    }

    /// <summary>
    /// Waits until every active bench worker has heartbeated since the override was written, then for that tick's
    /// reload. Each worker's heartbeat is read after the override commits and must move past that reading, so the
    /// wait compares the database's own stamps and never the bench's clock with the server's.
    /// </summary>
    private static async Task WaitForPolicyReloadAsync(IActaOperations operations, CancellationToken ct)
    {
        var query = new ListWorkersQuery(BenchHost.Namespace, WorkerStatusCode.Active, PageSize: 100);
        var before = (await operations.Workers.ListAsync(query, ct)).Items.ToDictionary(w => w.WorkerRef, w => w.LastSeenAtUtc);
        while (true)
        {
            var now = (await operations.Workers.ListAsync(query, ct)).Items;
            if (before.Count > 0 && before.All(b => now.Any(w => w.WorkerRef == b.Key && w.LastSeenAtUtc > b.Value)))
            {
                break;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
        await Task.Delay(PolicyReloadGrace, ct);
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
