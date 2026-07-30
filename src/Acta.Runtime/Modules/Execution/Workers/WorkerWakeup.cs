using System.Collections.Concurrent;
using Acta.Modules.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Acta.Modules.Execution.Workers;

/// <summary>
/// Auto-reset, coalescing async wake event. <see cref="Set"/> completes one pending waiter; with no
/// waiter pending it latches a single slot so the next <see cref="WaitAsync"/> returns immediately
/// once, so a wake raced against an in-flight re-read pass is never lost. Many sets between waits
/// collapse onto the one latch slot.
/// </summary>
/// <remarks>
/// Concurrency: a waiter that times out (or is cancelled) retires its own completion source under
/// the gate, and <see cref="Set"/> skips retired waiters, retrying until it either wakes a live one
/// or latches. Completed heads are swept on enqueue so an idle loop's repeated timeouts never grow
/// the queue. The delay task is cancelled on a wake, so no timer outlives its wait.
/// </remarks>
internal sealed class AsyncWakeSignal
{
    private readonly Queue<TaskCompletionSource> _waiters = new();
    private readonly object _gate = new();
    private bool _latched;

    public void Set()
    {
        while (true)
        {
            TaskCompletionSource? candidate;
            lock (_gate)
            {
                while (_waiters.Count > 0 && _waiters.Peek().Task.IsCompleted)
                {
                    _waiters.Dequeue();
                }

                if (_waiters.Count == 0)
                {
                    _latched = true;
                    return;
                }

                candidate = _waiters.Dequeue();
            }

            // A false result means the waiter retired itself between dequeue and here; loop so the
            // signal lands on the next live waiter (or the latch) instead of vanishing.
            if (candidate.TrySetResult())
            {
                return;
            }
        }
    }

    public async ValueTask<WorkerWakeupWaitResult> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        TaskCompletionSource waiter;
        lock (_gate)
        {
            if (_latched)
            {
                _latched = false;
                return WorkerWakeupWaitResult.Signaled;
            }

            while (_waiters.Count > 0 && _waiters.Peek().Task.IsCompleted)
            {
                _waiters.Dequeue();
            }

            waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Enqueue(waiter);
        }

        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(timeout, delayCts.Token);
        var winner = await Task.WhenAny(waiter.Task, delay);
        if (winner == waiter.Task)
        {
            // Cancel the delay so its timer dies now. A cancelled Task.Delay left unawaited is safe:
            // it transitions to Canceled, which never surfaces as an unobserved fault.
            await delayCts.CancelAsync();
            return WorkerWakeupWaitResult.Signaled;
        }

        // Timeout or cancellation: retire the waiter so a Set after this point latches. A failed
        // retire means Set won the race; consume that wake rather than dropping it.
        if (!waiter.TrySetCanceled())
        {
            return WorkerWakeupWaitResult.Signaled;
        }

        ct.ThrowIfCancellationRequested();
        return WorkerWakeupWaitResult.TimedOut;
    }
}

/// <summary>
/// Default <see cref="IWorkerWakeup"/>: same-process signaling with one <see cref="AsyncWakeSignal"/>
/// per channel name. Worker-namespace channels (bounded keyspace) allocate on publish, so a keyed wake
/// ahead of the first wait latches. <see cref="WorkerWakeupChannelKind.JobCompletion"/> channels
/// (unbounded keyspace) never allocate on publish: a wake reaches existing waiters only, and the entry
/// is removed when its last waiter leaves, so per-job wakes cannot grow the dictionary. An
/// <see cref="WorkerWakeupChannel.AllWorkerNamespaces"/> wake sets each known non-job channel.
/// Cross-process reach requires a transport implementation replacing this registration; processes
/// without one fall back to their poll floors for changes made elsewhere. Registered unconditionally,
/// so enqueue-only processes publish into it harmlessly (no waiters). The job-completion entry removal
/// races a concurrent new waiter by design: the loser waits on an orphaned entry and times out into its
/// poll floor, a permitted loss under the best-effort contract.
/// </summary>
internal sealed class InProcessWakeup : IWorkerWakeup
{
    private sealed class Entry(WorkerWakeupChannelKind kind)
    {
        public readonly AsyncWakeSignal Signal = new();
        public readonly WorkerWakeupChannelKind Kind = kind;
        public int Waiters;
    }

    private readonly ConcurrentDictionary<string, Entry> _channels = new(StringComparer.Ordinal);

    public ValueTask WakeAsync(WorkerWakeupChannel channel, WorkerWakeupReason reason, CancellationToken ct = default)
    {
        if (channel.Kind == WorkerWakeupChannelKind.AllWorkerNamespaces)
        {
            foreach (var entry in _channels.Values)
            {
                if (entry.Kind != WorkerWakeupChannelKind.JobCompletion)
                {
                    entry.Signal.Set();
                }
            }
        }
        else if (channel.AllocatesOnPublish)
        {
            _channels.GetOrAdd(channel.Name, static (_, kind) => new Entry(kind), channel.Kind).Signal.Set();
        }
        else if (_channels.TryGetValue(channel.Name, out var entry))
        {
            entry.Signal.Set();
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<WorkerWakeupWaitResult> WaitAsync(WorkerWakeupChannel channel, TimeSpan timeout, CancellationToken ct)
    {
        var entry = _channels.GetOrAdd(channel.Name, static (_, kind) => new Entry(kind), channel.Kind);
        if (channel.AllocatesOnPublish)
        {
            return await entry.Signal.WaitAsync(timeout, ct);
        }

        // Unbounded keyspace: the waiter owns the entry's lifetime. The keyed Remove only removes
        // THIS entry instance, so a fresh entry created by a racing waiter survives.
        Interlocked.Increment(ref entry.Waiters);
        try
        {
            return await entry.Signal.WaitAsync(timeout, ct);
        }
        finally
        {
            if (Interlocked.Decrement(ref entry.Waiters) == 0)
            {
                ((ICollection<KeyValuePair<string, Entry>>)_channels).Remove(new KeyValuePair<string, Entry>(channel.Name, entry));
            }
        }
    }
}

/// <summary>
/// The publish-side seam every waking call site uses, never <see cref="IWorkerWakeup"/> directly.
/// Centralizes what no transport can be trusted to promise: a wake never breaks its caller (all
/// failures, including the caller's own cancellation, are caught, logged, and counted), and publish
/// metrics are recorded once here so swapped transports cannot skew the counters. Every wake is
/// published after its durable mutation committed, so surfacing cancellation here would report
/// failure for an operation that already succeeded; delivery is best-effort and waiters keep a poll
/// floor. The wait side (claim loops, completion waits) consumes <see cref="IWorkerWakeup"/> directly.
/// </summary>
internal sealed class WorkerWakeupPublisher(IWorkerWakeup wakeup, ILogger<WorkerWakeupPublisher>? log = null, JobMetrics? metrics = null)
{
    private readonly ILogger _log = (ILogger?)log ?? NullLogger.Instance;

    public async ValueTask WakeAsync(WorkerWakeupChannel channel, WorkerWakeupReason reason, CancellationToken ct = default)
    {
        metrics?.RecordWakeupPublish(NamespaceTag(channel), ChannelTag(channel.Kind), ReasonTag(reason));
        try
        {
            await wakeup.WakeAsync(channel, reason, ct);
        }
        catch (Exception ex)
        {
            metrics?.RecordWakeupPublishFailure(NamespaceTag(channel), ChannelTag(channel.Kind), ReasonTag(reason), ex.GetType().Name);
            _log.LogWarning(ex, "Wake publish failed for '{Channel}'; relying on the waiter's poll floor.", channel.Name);
        }
    }

    // Stable low-cardinality tag values are not derived from enum display text, so an enum rename
    // cannot silently rename an operator-facing metric dimension. Job-completion channels omit the
    // namespace tag because a per-job value would explode tag cardinality.
    internal static string? NamespaceTag(WorkerWakeupChannel channel) =>
        channel.Kind switch
        {
            WorkerWakeupChannelKind.WorkerNamespace => channel.Name[WorkerWakeupChannel.WorkerNamespacePrefix.Length..],
            WorkerWakeupChannelKind.AllWorkerNamespaces => WorkerWakeupChannel.AllWorkerNamespacesName,
            _ => null,
        };

    internal static string ChannelTag(WorkerWakeupChannelKind kind) =>
        kind switch
        {
            WorkerWakeupChannelKind.WorkerNamespace => "worker_namespace",
            WorkerWakeupChannelKind.AllWorkerNamespaces => "all_worker_namespaces",
            WorkerWakeupChannelKind.JobCompletion => "job_completion",
            _ => "unknown",
        };

    internal static string ReasonTag(WorkerWakeupReason reason) =>
        reason switch
        {
            WorkerWakeupReason.WorkAvailable => "work_available",
            WorkerWakeupReason.HorizonChanged => "horizon_changed",
            WorkerWakeupReason.JobFinished => "job_finished",
            _ => "unknown",
        };

    internal static string WaitResultTag(WorkerWakeupWaitResult result) =>
        result switch
        {
            WorkerWakeupWaitResult.Signaled => "signaled",
            WorkerWakeupWaitResult.TimedOut => "timed_out",
            _ => "unknown",
        };
}
