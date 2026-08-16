using System.Diagnostics;
using Acta;

namespace Anvil.Bench;

/// <summary>
/// One benchmark scenario. A scenario owns its host lifecycle (it resets the schema, starts the real
/// runtime, drives load, and tears down), so each isolates exactly the cost it claims to.
/// </summary>
public interface IScenario
{
    /// <summary>The CLI name, e.g. <c>throughput</c>.</summary>
    string Name { get; }

    /// <summary>One-line description shown by <c>list</c>.</summary>
    string Description { get; }

    /// <summary>
    /// How many observed samples mark a complete cell. The runner reports <c>incomplete</c> below this.
    /// Defaults to the backlog size.
    /// </summary>
    int ExpectedObserved(CellParams p) => p.Jobs;

    /// <summary>
    /// Runs the scenario at the given parameters against the given schema and returns its metrics.
    /// Throws <see cref="BenchDbUnavailableException"/> if the database is unreachable.
    /// </summary>
    Task<CellMetrics> RunAsync(CellParams p, string schema, BenchConfig cfg, CancellationToken ct);
}

/// <summary>
/// Shared enqueue helpers. Chunks batches to stay under provider parameter limits, the same bound the
/// stress probe uses.
/// </summary>
internal static class Workload
{
    private const int EnqueueChunk = 1000;

    public static TimeSpan DrainDeadline { get; } = TimeSpan.FromMinutes(10);

    public static string? Pad(int bytes) => bytes > 0 ? new string('x', bytes) : null;

    /// <summary>
    /// Enqueues <paramref name="count"/> jobs in chunks, stamping each request with the current
    /// Stopwatch timestamp at submit time. Returns the elapsed enqueue window.
    /// </summary>
    public static async Task<TimeSpan> EnqueueAsync(
        IJobs jobs,
        int count,
        int payloadBytes,
        int? delaySeconds,
        CancellationToken ct,
        string jobName = BenchHost.JobName,
        string? exclusiveKey = null,
        int workMs = 0
    )
    {
        var pad = Pad(payloadBytes);
        var sw = Stopwatch.StartNew();
        var batch = new List<JobEnqueueRequest>(Math.Min(EnqueueChunk, count));
        for (var i = 0; i < count; i++)
        {
            batch.Add(
                new JobEnqueueRequest(
                    BenchHost.Namespace,
                    jobName,
                    BenchPayloads.Json(new BenchInput(Stopwatch.GetTimestamp(), pad, workMs)),
                    ExclusiveKey: exclusiveKey,
                    DelaySeconds: delaySeconds
                )
            );
            if (batch.Count == EnqueueChunk || i == count - 1)
            {
                await jobs.EnqueueBatchAsync(batch, ct);
                batch.Clear();
            }
        }
        sw.Stop();
        return sw.Elapsed;
    }

    /// <summary>
    /// Enqueues <paramref name="count"/> jobs one call at a time, returning each call's round-trip ticks
    /// and the total window. This is the realistic per-request enqueue path (no batching).
    /// </summary>
    public static async Task<(long[] Ticks, TimeSpan Elapsed)> EnqueueEachAsync(
        IJobs jobs,
        int count,
        int payloadBytes,
        int? delaySeconds,
        CancellationToken ct
    )
    {
        var pad = Pad(payloadBytes);
        var ticks = new long[count];
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < count; i++)
        {
            var start = Stopwatch.GetTimestamp();
            await jobs.EnqueueAsync(
                new JobEnqueueRequest(
                    BenchHost.Namespace,
                    BenchHost.JobName,
                    BenchPayloads.Json(new BenchInput(start, pad)),
                    DelaySeconds: delaySeconds
                ),
                ct
            );
            ticks[i] = Stopwatch.GetTimestamp() - start;
        }
        sw.Stop();
        return (ticks, sw.Elapsed);
    }

    /// <summary>Latency percentiles over (entry - reference) for every recorded sample.</summary>
    public static (double P50, double P95, double P99, double Max, double Mean) Latencies(
        BenchSink sink,
        Func<(long Enqueued, long Entry), long> delta
    )
    {
        var ticks = sink.Samples.ToArray().Select(delta).Where(t => t >= 0).ToArray();
        return Stats.Percentiles(ticks);
    }

    /// <summary>
    /// Enqueues a backlog future-dated behind a computed horizon so nothing is Ready, waits out the
    /// horizon, and returns the enqueue-phase elapsed window plus the Stopwatch timestamp at which the rows
    /// became due. Callers time the drain from now and read queue-residence latency as
    /// (sample.Entry - releaseStamp).
    /// </summary>
    public static async Task<(TimeSpan Enqueue, long ReleaseStamp)> PreloadBehindHorizonAsync(
        IJobs jobs,
        int count,
        int payloadBytes,
        CancellationToken ct,
        string? exclusiveKey = null,
        string jobName = BenchHost.JobName,
        int workMs = 0
    )
    {
        var horizonSeconds = Math.Max(5, count / 25_000 + 3);
        var enqueueStart = Stopwatch.GetTimestamp();
        var enqueue = await EnqueueAsync(
            jobs,
            count,
            payloadBytes,
            horizonSeconds,
            ct,
            jobName: jobName,
            exclusiveKey: exclusiveKey,
            workMs: workMs
        );
        var releaseStamp = enqueueStart + (long)(horizonSeconds * Stopwatch.Frequency);
        var remainingMs = Stats.Ms(releaseStamp - Stopwatch.GetTimestamp());
        if (remainingMs > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(remainingMs), ct);
        }
        return (enqueue, releaseStamp);
    }

    /// <summary>
    /// Paces enqueues to hit <paramref name="ratePerSec"/> over <paramref name="duration"/>, emitting a
    /// small batch each ~50ms tick and sleeping to stay on schedule. Unlike <see cref="EnqueueAsync"/>
    /// (which dumps the whole backlog up front), this models arrival-over-time for the soak/spike/overload
    /// shapes. Each request is stamped with the current Stopwatch timestamp at submit so the sink reads
    /// true queue-residence latency. Returns the total number of jobs emitted.
    /// </summary>
    public static async Task<int> ProduceConstantAsync(
        IJobs jobs,
        int ratePerSec,
        TimeSpan duration,
        int payloadBytes,
        CancellationToken ct,
        string jobName = BenchHost.JobName
    )
    {
        var pad = Pad(payloadBytes);
        const int tickMs = 50;
        var perTick = Math.Max(1, ratePerSec * tickMs / 1000);
        var sw = Stopwatch.StartNew();
        var emitted = 0;
        while (sw.Elapsed < duration && !ct.IsCancellationRequested)
        {
            var batch = new List<JobEnqueueRequest>(perTick);
            for (var i = 0; i < perTick; i++)
            {
                batch.Add(
                    new JobEnqueueRequest(BenchHost.Namespace, jobName, BenchPayloads.Json(new BenchInput(Stopwatch.GetTimestamp(), pad)))
                );
            }
            await jobs.EnqueueBatchAsync(batch, ct);
            emitted += batch.Count;
            var nextTick = (emitted / perTick) * tickMs;
            var sleep = nextTick - (int)sw.ElapsedMilliseconds;
            if (sleep > 0)
            {
                await Task.Delay(sleep, ct);
            }
        }
        return emitted;
    }

    public static async Task<bool> WaitForDrain(BenchSink sink, CancellationToken ct)
    {
        try
        {
            await sink.Completed.WaitAsync(DrainDeadline, ct);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}

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

/// <summary>
/// Claim + execute + complete only, write cost removed. Parameterized by Workers (N>1 runs N in-process
/// workers draining one preloaded backlog - the claim-contention sweep); SharedKey makes every job
/// carry one exclusive key so the lock serializes them (the contention/fairness probe). The whole backlog
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
            // Fast heartbeat so lease extension runs inside short benchmark cells.
            LeaseTtlSeconds = 30,
        };

        if (p.Workers > 1)
        {
            await using var cluster = await BenchCluster.StartAsync(template, p.Workers, ct);
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
            exclusiveKey: cfg.SharedKey ? SharedKey : null,
            jobName: BenchHost.WorkloadJobName(cfg.AuditOn),
            workMs: cfg.WorkMs
        );

        var drain = Stopwatch.StartNew();
        await Workload.WaitForDrain(sink, ct);
        drain.Stop();

        var (P50, P95, P99, Max, Mean) = Workload.Latencies(sink, s => s.Entry - releaseStamp);
        var overheadUs = !sink.Samples.IsEmpty ? drain.Elapsed.TotalSeconds / sink.Samples.Count * 1e6 : 0;
        var extra = new Dictionary<string, double> { ["overheadUsPerJob"] = overheadUs, ["workers"] = workers };
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

/// <summary>
/// Per-job round-trip latency at concurrency one: enqueue a single job, let the running worker pick it
/// up, read its poll-free handler-entry latency from the sink, repeat. Captures the durable round-trip
/// cost the fsync model predicts, free of batching and queue depth.
/// </summary>
public sealed class LatencyScenario : IScenario
{
    public string Name => "latency";

    public string Description => "Single-job round-trip latency at concurrency 1 (p50/p95/p99 ms).";

    public int ExpectedObserved(CellParams p) => p.Iterations;

    public async Task<CellMetrics> RunAsync(CellParams p, string schema, BenchConfig cfg, CancellationToken ct)
    {
        await using var host = await BenchHost.StartAsync(
            new BenchHostOptions
            {
                Provider = p.Provider,
                Schema = schema,
                Executors = Math.Max(1, p.Executors),
                ClaimBatch = Math.Max(1, p.ClaimBatch),
                Profile = p.Profile,
            },
            ct
        );
        host.Sink.Expect(int.MaxValue);

        var pad = Workload.Pad(p.PayloadBytes);
        var deadline = Stopwatch.StartNew();
        var perJobDeadline = TimeSpan.FromSeconds(30);

        async Task RunOne()
        {
            var target = host.Sink.Samples.Count + 1;
            await host.Jobs.EnqueueAsync(
                new JobEnqueueRequest(
                    BenchHost.Namespace,
                    BenchHost.JobName,
                    BenchPayloads.Json(new BenchInput(Stopwatch.GetTimestamp(), pad))
                ),
                ct
            );
            var jobStart = Stopwatch.StartNew();
            while (host.Sink.Samples.Count < target && jobStart.Elapsed < perJobDeadline)
            {
                await Task.Delay(1, ct);
            }
        }

        for (var w = 0; w < p.Iterations / 10 + 1; w++)
        {
            await RunOne();
        }
        var cut = host.Sink.Samples.Count;

        for (var i = 0; i < p.Iterations; i++)
        {
            await RunOne();
        }

        var measured = host.Sink.Samples.ToArray().Skip(cut).Select(s => s.Entry - s.Enqueued).Where(t => t >= 0).ToArray();
        var (P50, P95, P99, Max, Mean) = Stats.Percentiles(measured);
        return new CellMetrics(
            EnqueueRatePerSec: 0,
            EndToEndRatePerSec: Stats.RatePerSec(measured.Length, deadline.Elapsed.TotalSeconds),
            DrainRatePerSec: 0,
            LatencyP50Ms: P50,
            LatencyP95Ms: P95,
            LatencyP99Ms: P99,
            LatencyMaxMs: Max,
            LatencyMeanMs: Mean,
            EnqueueSeconds: 0,
            DrainSeconds: deadline.Elapsed.TotalSeconds,
            JobsObserved: measured.Length
        );
    }
}

/// <summary>
/// Single-call enqueue / fleet insert throughput: <c>Workers</c> concurrent producers each enqueue a share
/// of the jobs one call at a time (the per-request path, <c>EnqueueAsync</c>) into the one shared namespace,
/// with a far horizon so nothing drains. <c>Workers=1</c> is the single-producer per-call rate + latency;
/// <c>Workers=4/16</c> measures how many jobs a fleet of single-call producers can insert under write
/// contention. Reports aggregate enqueue/sec and per-call latency percentiles across the fleet.
/// </summary>
public sealed class EnqueueScenario : IScenario
{
    private const int IdleHorizonSeconds = 3600;

    public string Name => "enqueue";

    public string Description => "Single-call enqueue fleet insert throughput + per-call latency (N producers, no drain).";

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
            },
            ct
        );

        // Fan out Workers producers, each looping single-call enqueues concurrently into the shared
        // namespace; aggregate wall-clock is the fleet rate, per-call ticks pooled for the latency spread.
        var producers = Math.Max(1, p.Workers);
        var sw = Stopwatch.StartNew();
        var results = await Task.WhenAll(
            Enumerable
                .Range(0, producers)
                .Select(i =>
                    Workload.EnqueueEachAsync(
                        host.Jobs,
                        p.Jobs / producers + (i < p.Jobs % producers ? 1 : 0),
                        p.PayloadBytes,
                        IdleHorizonSeconds,
                        ct
                    )
                )
        );
        sw.Stop();

        var enqueued = p.Jobs;
        var (P50, P95, P99, Max, Mean) = Stats.Percentiles([.. results.SelectMany(r => r.Ticks)]);
        return new CellMetrics(
            EnqueueRatePerSec: Stats.RatePerSec(enqueued, sw.Elapsed.TotalSeconds),
            EndToEndRatePerSec: 0,
            DrainRatePerSec: 0,
            LatencyP50Ms: P50,
            LatencyP95Ms: P95,
            LatencyP99Ms: P99,
            LatencyMaxMs: Max,
            LatencyMeanMs: Mean,
            EnqueueSeconds: sw.Elapsed.TotalSeconds,
            DrainSeconds: 0,
            JobsObserved: enqueued,
            Extra: new Dictionary<string, double> { ["producers"] = producers }
        );
    }
}

/// <summary>
/// Batch-enqueue / fleet insert throughput: <c>Workers</c> concurrent producers each <c>EnqueueBatchAsync</c>
/// a share of the jobs into the one shared namespace, with a far horizon so nothing drains during the
/// measurement. <c>Workers=1</c> is the single-producer batch rate; <c>Workers=4/16</c> measures how many
/// jobs a fleet can insert under shared-table write contention (index leaf latches, identity/sequence, the
/// parent lock, tempdb TVP materialization on SQL Server). Isolates the batch path the per-call
/// <see cref="EnqueueScenario"/> never exercises and that throughput only touches in a sub-second,
/// drain-overlapped window, so a batch-routine regression surfaces as a gated jobs/sec drop.
/// </summary>
public sealed class EnqueueBatchScenario : IScenario
{
    // This scenario writes rows and never drains them, so it runs roughly two orders of magnitude
    // faster per job than throughput or drain. At the shared preset job count a cell finished in
    // about 0.4s, short enough that process warmup and OS scheduling dominated it: repeats of the
    // same commit spread 30-50%, which is wider than any regression the cell is meant to catch. It
    // therefore takes its own row count (BenchPreset.EnqueueBatchJobs, two orders of magnitude above
    // the shared count) so the measured window is seconds rather than milliseconds.
    //
    // Rates stay in the same units (enq/s over the aggregate producer wall clock), but they are NOT
    // comparable across the resizing: a baseline captured before it measured a different amount of
    // work, and the per-row cost changes with volume (page splits, index growth, WAL churn). Compare
    // enqueue-batch numbers only against baselines whose cell key carries the same job count.
    private const int IdleHorizonSeconds = 3600;

    public string Name => "enqueue-batch";

    public string Description => "Batch-enqueue fleet insert throughput (N producers, one namespace, no drain).";

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
            },
            ct
        );

        // Fan out producer tasks, each batch-enqueuing its share concurrently into the shared namespace.
        // Aggregate wall-clock over the fleet is the fleet insert rate; a single worker uses one producer.
        var producers = Math.Max(1, p.Workers);
        var sw = Stopwatch.StartNew();
        await Task.WhenAll(
            Enumerable
                .Range(0, producers)
                .Select(i =>
                    Workload.EnqueueAsync(
                        host.Jobs,
                        p.Jobs / producers + (i < p.Jobs % producers ? 1 : 0),
                        p.PayloadBytes,
                        IdleHorizonSeconds,
                        ct
                    )
                )
        );
        sw.Stop();

        var enqueued = p.Jobs;
        return new CellMetrics(
            EnqueueRatePerSec: Stats.RatePerSec(enqueued, sw.Elapsed.TotalSeconds),
            EndToEndRatePerSec: 0,
            DrainRatePerSec: 0,
            LatencyP50Ms: 0,
            LatencyP95Ms: 0,
            LatencyP99Ms: 0,
            LatencyMaxMs: 0,
            LatencyMeanMs: 0,
            EnqueueSeconds: sw.Elapsed.TotalSeconds,
            DrainSeconds: 0,
            JobsObserved: enqueued,
            Extra: new Dictionary<string, double> { ["producers"] = producers }
        );
    }
}

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
            ct
        );
        var noOp = await MeasureAsync(p, schema, poll, BenchWakeupMode.NoOp, null, samples, resetSchema: true, ct);

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
            var redis = await MeasureAsync(p, schema, poll, BenchWakeupMode.Redis, cfg.RedisConfig, samples, resetSchema: true, ct);
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
        };
        await using var host = await BenchHost.StartAsync(opt, ct);
        host.Sink.Expect(int.MaxValue);

        var perJobDeadline = poll + TimeSpan.FromSeconds(5);
        for (var i = 0; i < samples; i++)
        {
            var target = host.Sink.Samples.Count + 1;
            await host.Jobs.EnqueueAsync(
                new JobEnqueueRequest(BenchHost.Namespace, BenchHost.JobName, BenchPayloads.Json(new BenchInput(Stopwatch.GetTimestamp()))),
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

/// <summary>
/// Dashboard list latency on a large table: batch-enqueue a big backlog (rows stay future-dated so they
/// are never executed), then time first-page and deep-cursor <c>ListJobsAsync</c> reads.
/// </summary>
public sealed class QueryScenario : IScenario
{
    private const int DefaultRows = 1_000_000;
    private const int IdleHorizonSeconds = 36_000;
    private const int Rounds = 10;
    private const int PageSize = 100;

    public string Name => "query";

    public string Description => "Dashboard list latency on a large backlog.";

    public int ExpectedObserved(CellParams p) => 1;

    public async Task<CellMetrics> RunAsync(CellParams p, string schema, BenchConfig cfg, CancellationToken ct)
    {
        var rows = p.Rows > 0 ? p.Rows : DefaultRows;
        await using var host = await BenchHost.StartAsync(p.Provider, schema, executors: 1, claimBatch: 1, ct);

        await Workload.EnqueueAsync(host.Jobs, rows, p.PayloadBytes, IdleHorizonSeconds, ct);

        var ms = new List<long>();
        for (var round = 0; round < Rounds; round++)
        {
            var sw = Stopwatch.GetTimestamp();
            var page = await host.Queries.Ledger.ListJobsAsync(
                new ListJobsQuery(JobNamespace: BenchHost.Namespace, PageSize: PageSize),
                ct
            );
            ms.Add(Stopwatch.GetTimestamp() - sw);

            var cursor = page.NextCursor;
            for (var depth = 0; depth < 3 && cursor is not null; depth++)
            {
                var s2 = Stopwatch.GetTimestamp();
                var deep = await host.Queries.Ledger.ListJobsAsync(
                    new ListJobsQuery(JobNamespace: BenchHost.Namespace, PageSize: PageSize, Cursor: cursor),
                    ct
                );
                ms.Add(Stopwatch.GetTimestamp() - s2);
                cursor = deep.NextCursor;
            }
        }

        var (P50, P95, P99, Max, Mean) = Stats.Percentiles([.. ms]);
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
            JobsObserved: ms.Count,
            Extra: new Dictionary<string, double> { ["rows"] = rows, ["queryP95Ms"] = P95 }
        );
    }
}

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
            await host.Jobs.EnqueueAsync(new JobEnqueueRequest(BenchHost.Namespace, BenchHost.RetentionJobName), ct);
            await Task.Delay(500, ct);
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

/// <summary>
/// The known scenarios, by CLI name.
/// </summary>
public static class ScenarioRegistry
{
    public static readonly IReadOnlyList<IScenario> All =
    [
        new ThroughputScenario(),
        new DrainScenario(),
        new LatencyScenario(),
        new EnqueueScenario(),
        new EnqueueBatchScenario(),
        new RecoveryScenario(),
        new WakeupScenario(),
        new QueryScenario(),
        new PurgeScenario(),
        new LoadProfileScenario(),
    ];

    public static IScenario? Find(string name) => All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
}
