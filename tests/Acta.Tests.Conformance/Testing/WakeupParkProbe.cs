using System.Collections.Concurrent;
using Acta.Runtime.Modules.Execution.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// The real in-process wakeup plus three observations a timing fact needs and a clock cannot give it.
/// <see cref="Parked"/> completes the first time a claim loop enters its namespace wait, so an act
/// meant to interrupt that sleep can be ordered after the loop is provably in it: under a slow first
/// claim the loop is still mid-claim, claims the row directly, and the fact passes without a wake ever
/// mattering. <see cref="WaitsOn"/> then reports how each sleep ENDED, which is the fact itself: an
/// idle sleep ends in a wake or in its poll timeout and in nothing else, so reading the outcome
/// answers "wake or poll?" exactly, where an elapsed-time budget only estimates it and loses the
/// estimate on a slow runner. <see cref="RequestedTimeoutsOn"/> reports the other half - how long each
/// wait ASKED to sleep - so a fact about a caller clamping its own sleep reads the clamp directly
/// instead of inferring it from how long the call took.
/// </summary>
internal sealed class WakeupParkProbe : IWorkerWakeup
{
    private readonly InProcessWakeup _inner = new();
    private readonly TaskCompletionSource _parked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<(WorkerWakeupChannelKind Kind, WorkerWakeupWaitStatus Result)> _waits = new();
    private readonly ConcurrentQueue<(WorkerWakeupChannelKind Kind, TimeSpan Timeout)> _requested = new();

    /// <summary>Completes once a claim loop has parked in its namespace wait.</summary>
    public Task Parked => _parked.Task;

    /// <summary>
    /// How each completed wait on <paramref name="kind"/> ended, in completion order. A wait ended by
    /// cancellation (loop shutdown) never completes and is not reported.
    /// </summary>
    public IReadOnlyList<WorkerWakeupWaitStatus> WaitsOn(WorkerWakeupChannelKind kind) =>
        [.. _waits.Where(w => w.Kind == kind).Select(w => w.Result)];

    /// <summary>
    /// The timeout every wait on <paramref name="kind"/> was issued with, in call order. Recorded on
    /// entry, so a wait still in flight is included.
    /// </summary>
    public IReadOnlyList<TimeSpan> RequestedTimeoutsOn(WorkerWakeupChannelKind kind) =>
        [.. _requested.Where(w => w.Kind == kind).Select(w => w.Timeout)];

    public ValueTask WakeAsync(WorkerWakeupChannel channel, WorkerWakeupReason reason, CancellationToken ct = default) =>
        _inner.WakeAsync(channel, reason, ct);

    public async ValueTask<WorkerWakeupWaitStatus> WaitAsync(WorkerWakeupChannel channel, TimeSpan timeout, CancellationToken ct)
    {
        _requested.Enqueue((channel.Kind, timeout));

        // Reported before the inner wait enqueues its waiter. A wake published in that window latches
        // on this publish-allocating channel and satisfies the wait at once, so the report is never
        // early enough to lose one.
        if (channel.Kind == WorkerWakeupChannelKind.WorkerNamespace)
        {
            _parked.TrySetResult();
        }

        var result = await _inner.WaitAsync(channel, timeout, ct);
        _waits.Enqueue((channel.Kind, result));
        return result;
    }
}

internal static class WakeupParkProbeServiceCollectionExtensions
{
    public static WakeupParkProbe AddWakeupParkProbe(this IServiceCollection services)
    {
        var probe = new WakeupParkProbe();
        services.Replace(ServiceDescriptor.Singleton<IWorkerWakeup>(probe));
        return probe;
    }
}
