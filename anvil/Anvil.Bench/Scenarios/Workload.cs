using System.Collections.Concurrent;
using System.Diagnostics;
using Acta;

namespace Anvil.Bench;

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
    /// Stopwatch timestamp at submit time. <paramref name="laneOf"/> names each job's lane by its index,
    /// and <paramref name="priority"/> overrides the definition's priority. Returns the elapsed enqueue window.
    /// </summary>
    public static async Task<TimeSpan> EnqueueAsync(
        IJobs jobs,
        int count,
        int payloadBytes,
        int? delaySeconds,
        CancellationToken ct,
        string jobName = BenchHost.JobName,
        string? concurrencyKey = null,
        int workMs = 0,
        Func<int, string?>? laneOf = null,
        JobPriorityCode? priority = null
    )
    {
        var pad = Pad(payloadBytes);
        var sw = Stopwatch.StartNew();
        var batch = new List<EnqueueJobRequest>(Math.Min(EnqueueChunk, count));
        for (var i = 0; i < count; i++)
        {
            batch.Add(
                new EnqueueJobRequest(
                    BenchHost.Namespace,
                    jobName,
                    BenchPayloads.Json(new BenchInput(Stopwatch.GetTimestamp(), pad, workMs)),
                    ConcurrencyKey: concurrencyKey,
                    Lane: laneOf?.Invoke(i),
                    Priority: priority,
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
                new EnqueueJobRequest(
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
    ) => Latencies(sink.Samples, delta);

    /// <summary>
    /// The same percentiles over one chosen sample queue, so a cell that fills both of a sink's queues
    /// can read each workload's distribution on its own.
    /// </summary>
    public static (double P50, double P95, double P99, double Max, double Mean) Latencies(
        ConcurrentQueue<(long Enqueued, long Entry)> samples,
        Func<(long Enqueued, long Entry), long> delta
    )
    {
        var ticks = samples.ToArray().Select(delta).Where(t => t >= 0).ToArray();
        return Stats.Percentiles(ticks);
    }

    /// <summary>
    /// Enqueues a backlog future-dated behind a computed horizon so nothing is Ready, waits out the
    /// horizon, and returns the enqueue-phase elapsed window plus the Stopwatch timestamp at which the rows
    /// became due. Callers time the drain from now and read queue-residence latency as
    /// (sample.Entry - releaseStamp). <paramref name="alongsideCount"/> rides the same horizon, so a cell
    /// can release a second workload against the same executors at the same instant. <paramref name="priority"/>
    /// overrides the main workload's priority.
    /// </summary>
    public static async Task<(TimeSpan Enqueue, long ReleaseStamp)> PreloadBehindHorizonAsync(
        IJobs jobs,
        int count,
        int payloadBytes,
        CancellationToken ct,
        string? concurrencyKey = null,
        string jobName = BenchHost.JobName,
        int workMs = 0,
        int alongsideCount = 0,
        string alongsideJobName = BenchHost.JobName,
        JobPriorityCode? priority = null
    )
    {
        var horizonSeconds = Math.Max(5, (count + alongsideCount) / 25_000 + 3);
        var enqueueStart = Stopwatch.GetTimestamp();
        var enqueue = await EnqueueAsync(
            jobs,
            count,
            payloadBytes,
            horizonSeconds,
            ct,
            jobName: jobName,
            concurrencyKey: concurrencyKey,
            workMs: workMs,
            priority: priority
        );
        if (alongsideCount > 0)
        {
            enqueue += await EnqueueAsync(
                jobs,
                alongsideCount,
                payloadBytes,
                horizonSeconds,
                ct,
                jobName: alongsideJobName,
                workMs: workMs
            );
        }
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
            var batch = new List<EnqueueJobRequest>(perTick);
            for (var i = 0; i < perTick; i++)
            {
                batch.Add(
                    new EnqueueJobRequest(BenchHost.Namespace, jobName, BenchPayloads.Json(new BenchInput(Stopwatch.GetTimestamp(), pad)))
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
