namespace Acta;

/// <summary>
/// The wake seam: a wake from a state-changing operation interrupts a waiter's sleep so the change is discovered
/// without waiting out its poll floor. A wake carries no payload ("go look") and never decides what runs: the row is
/// the message and the waiter re-reads the database. Claim loops wait on <c>WorkerNamespace</c>, completion waiters
/// on <c>JobCompletion</c>, <c>AllWorkerNamespaces</c> satisfies any worker-namespace wait, and every waiter keeps a
/// poll floor for lost wakes. Delivery is best-effort (duplicates, misses, spurious returns, no ordering) and
/// coalescing (a wake with no waiter latches once); an implementation honors the wait timeout and swallows transport failures.
/// </summary>
public interface IWorkerWakeup
{
    /// <summary>
    /// Signal the waiters of <paramref name="channel"/> that its underlying state may have changed.
    /// Returns quickly; <paramref name="reason"/> is diagnostic only.
    /// </summary>
    ValueTask WakeAsync(WorkerWakeupChannel channel, WorkerWakeupReason reason, CancellationToken ct = default);

    /// <summary>
    /// Wait until a wake for <paramref name="channel"/> arrives, <paramref name="timeout"/>
    /// elapses, or <paramref name="ct"/> cancels.
    /// </summary>
    ValueTask<WorkerWakeupWaitStatus> WaitAsync(WorkerWakeupChannel channel, TimeSpan timeout, CancellationToken ct);
}
