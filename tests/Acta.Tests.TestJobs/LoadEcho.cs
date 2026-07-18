using System.Collections.Concurrent;
using System.Diagnostics;
using Acta;

namespace TestJobs;

public sealed record LoadEcho(long EnqueuedTimestamp);

public sealed record LoadEchoResult(int Ok);

/// <summary>
/// Per-run collector of end-to-end job latencies (enqueue stamp to handler entry), in Stopwatch ticks.
/// Registered as a per-spec singleton, so parallel provider specs accumulate independently; the sample
/// count doubles as the exactly-once proof (one entry per handler invocation).
/// </summary>
public sealed class LoadLatencySink
{
    /// <summary>End-to-end latency samples in Stopwatch ticks, one per executed job.</summary>
    public ConcurrentQueue<long> ElapsedTicks { get; } = new();
}

/// <summary>
/// Stress-load handler: records its end-to-end latency from the enqueue stamp carried in the payload,
/// then returns. Allocation-light so the measured time is framework plus DB cost, not handler work.
/// </summary>
public sealed class LoadEchoHandler(LoadLatencySink sink)
{
    [Job("load-echo")]
    public LoadEchoResult Run(LoadEcho input)
    {
        sink.ElapsedTicks.Enqueue(Stopwatch.GetTimestamp() - input.EnqueuedTimestamp);
        return new LoadEchoResult(1);
    }
}
