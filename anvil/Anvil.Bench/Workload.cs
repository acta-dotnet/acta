using System.Collections.Concurrent;
using System.Diagnostics;
using Acta;

namespace Anvil.Bench;

/// <summary>
/// The benchmark workload payload. <c>EnqueuedTicks</c> carries the enqueue-side
/// <c>Stopwatch.GetTimestamp()</c> so the handler can compute end-to-end latency; <c>Pad</c> inflates
/// the serialized size for payload-size sweeps and is otherwise null.
/// </summary>
public sealed record BenchInput(long EnqueuedTicks, string? Pad = null, int WorkMs = 0);

/// <summary>
/// The benchmark result payload. Trivial; present so the workload also exercises the output-serialize
/// and result-persist path, not just input deserialize.
/// </summary>
public sealed record BenchResultPayload(int Ok);

/// <summary>
/// Per-run sink shared by every handler invocation. Records one sample per execution (enqueue stamp
/// plus handler-entry stamp), so the sample count is also the exactly-once and full-drain proof, and
/// signals a completion barrier when the expected count is reached.
/// </summary>
/// <remarks>
/// Registered as a DI singleton so all handler instances accumulate into one collector. A cluster
/// injects one shared instance into every host so multi-worker drains converge on one count. Reset
/// between cells by constructing a fresh host (and therefore a fresh sink) per cell.
/// </remarks>
public sealed class BenchSink
{
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _remaining = int.MaxValue;

    /// <summary>One sample per executed job: the carried enqueue stamp and the handler-entry stamp, both Stopwatch ticks.</summary>
    public ConcurrentQueue<(long Enqueued, long Entry)> Samples { get; } = new();

    /// <summary>Completes once the expected number of jobs have run.</summary>
    public Task Completed => _done.Task;

    /// <summary>
    /// Arms the completion barrier for <paramref name="count"/> executions. Call before enqueuing so a
    /// fast worker cannot finish before the target is set.
    /// </summary>
    public void Expect(int count) => Volatile.Write(ref _remaining, count);

    /// <summary>
    /// Records one execution and releases the barrier when the armed count is reached.
    /// </summary>
    public void Record(long enqueuedTicks)
    {
        Samples.Enqueue((enqueuedTicks, Stopwatch.GetTimestamp()));
        if (Interlocked.Decrement(ref _remaining) == 0)
        {
            _done.TrySetResult();
        }
    }
}

/// <summary>
/// Identifies which host in a <see cref="BenchCluster"/> a handler instance belongs to, so the
/// recovery scenario can tell which worker entered the blocking attempt and then kill it. Registered
/// as a distinct singleton per host.
/// </summary>
public sealed class BenchWorkerId
{
    public int Value { get; init; }
}

/// <summary>
/// Coordinates the worker-kill recovery scenario across the cluster's shared DI. The first worker to
/// execute the probe job blocks forever (simulating a hung process whose lease will lapse); the
/// re-execution after the lease is stolen takes the fast path and records, so the elapsed time is the
/// system's recovery latency.
/// </summary>
public sealed class RecoveryCoordinator
{
    /// <summary>A task that never completes; the first attempt returns it to hang the worker.</summary>
    public static readonly Task BlockForever = new TaskCompletionSource<object?>().Task;

    private readonly TaskCompletionSource<int> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _claimed;

    /// <summary>Completes with the host id of the worker that took the first (blocking) attempt.</summary>
    public Task<int> Entered => _entered.Task;

    /// <summary>
    /// True exactly once, for the first attempt: the caller must then block on
    /// <see cref="BlockForever"/>. Every later attempt (the post-steal re-execution) returns false and
    /// records normally.
    /// </summary>
    public bool EnterFirstAttempt(int hostId)
    {
        if (Interlocked.Exchange(ref _claimed, 1) == 0)
        {
            _entered.TrySetResult(hostId);
            return true;
        }
        return false;
    }
}

/// <summary>
/// The default benchmark handler. Allocation-light so the measured time is framework plus database
/// cost, not handler work.
/// </summary>
public sealed class BenchHandler(BenchSink sink)
{
    // Audit off so the headline number measures the claim/execute/complete path, not per-job audit
    // event writes. The purge scenario uses the audit-on handler below to generate events.
    [Job("bench-run", AuditLevel = JobAuditLevelCode.Off)]
    public async Task<BenchResultPayload> Run(BenchInput input, CancellationToken ct)
    {
        sink.Record(input.EnqueuedTicks);
        if (input.WorkMs > 0)
        {
            await Task.Delay(input.WorkMs, ct);
        }

        return new BenchResultPayload(1);
    }
}

/// <summary>
/// The audit-on twin of <see cref="BenchHandler"/>: byte-for-byte the same handler body, only the audit
/// level differs (default = on), so an off-vs-on capture isolates the per-job event-write cost. Also
/// seeds the events the purge scenario deletes.
/// </summary>
public sealed class BenchAuditHandler(BenchSink sink)
{
    [Job("bench-audit")]
    public async Task<BenchResultPayload> Run(BenchInput input, CancellationToken ct)
    {
        sink.Record(input.EnqueuedTicks);
        if (input.WorkMs > 0)
        {
            await Task.Delay(input.WorkMs, ct);
        }

        return new BenchResultPayload(1);
    }
}

/// <summary>
/// The recovery probe handler. The first worker to run it hangs forever (its lease lapses and the job
/// is stolen); the stealing worker's re-execution records, ending the recovery clock. Returns a Task
/// and ignores cancellation deliberately so an abruptly-disposed host leaves the job stuck Executing.
/// </summary>
public sealed class BenchBlockingHandler(RecoveryCoordinator recovery, BenchSink sink, BenchWorkerId workerId)
{
    [Job("bench-block", AuditLevel = JobAuditLevelCode.Off)]
    public Task Run(BenchInput input)
    {
        if (recovery.EnterFirstAttempt(workerId.Value))
        {
            return RecoveryCoordinator.BlockForever;
        }
        sink.Record(input.EnqueuedTicks);
        return Task.CompletedTask;
    }
}
