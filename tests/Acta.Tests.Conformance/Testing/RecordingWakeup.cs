using System.Collections.Concurrent;
using Acta.Runtime.Modules.Execution.Workers;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// The real in-process wakeup with every publish recorded. A pass that makes work claimable is
/// required to tell a worker so, and the published channel is the only externally visible half of
/// that: reading it names which channel was woken, where an awakened loop only shows that something
/// was.
/// </summary>
internal sealed class RecordingWakeup : IWorkerWakeup
{
    private readonly InProcessWakeup _inner = new();
    private readonly ConcurrentQueue<WorkerWakeupChannel> _published = new();
    private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes on the first publish, so a test can order itself after a background wake.</summary>
    public Task FirstPublish => _first.Task;

    /// <summary>Every channel published so far, in publish order.</summary>
    public IReadOnlyList<WorkerWakeupChannel> Published => [.. _published];

    public ValueTask WakeAsync(WorkerWakeupChannel channel, WorkerWakeupReason reason, CancellationToken ct = default)
    {
        _published.Enqueue(channel);
        _first.TrySetResult();
        return _inner.WakeAsync(channel, reason, ct);
    }

    public ValueTask<WorkerWakeupWaitStatus> WaitAsync(WorkerWakeupChannel channel, TimeSpan timeout, CancellationToken ct) =>
        _inner.WaitAsync(channel, timeout, ct);
}
