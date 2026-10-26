using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Acta;

/// <summary>
/// The publish-side seam every waking call site uses, never <see cref="IWorkerWakeup"/> directly. It centralizes what
/// no transport can be trusted to promise: a wake never breaks its caller (every failure, the caller's own
/// cancellation and a throwing metric listener or logger included, is caught), and publish metrics are recorded once
/// here so a swapped transport cannot skew them. Every wake is published after its durable mutation committed, so
/// surfacing a failure would report one for an operation that succeeded; delivery is best-effort and waiters keep a
/// poll floor. The wait side (claim loops, completion waits) consumes <see cref="IWorkerWakeup"/> directly.
/// </summary>
internal sealed class WorkerWakeupPublisher(IWorkerWakeup wakeup, ILogger<WorkerWakeupPublisher>? log = null, ActaMetrics? metrics = null)
{
    private readonly ILogger _log = (ILogger?)log ?? NullLogger.Instance;

    public async ValueTask WakeAsync(WorkerWakeupChannel channel, WorkerWakeupReason reason, CancellationToken ct = default)
    {
        try
        {
            metrics?.RecordWakeupPublish(NamespaceTag(channel), ChannelTag(channel.Kind), ReasonTag(reason));
            await wakeup.WakeAsync(channel, reason, ct);
        }
        catch (Exception ex)
        {
            try
            {
                metrics?.RecordWakeupPublishFailure(NamespaceTag(channel), ChannelTag(channel.Kind), ReasonTag(reason), ex.GetType().Name);
                _log.LogWarning(ex, "Wake publish failed for channel ({Detail}); relying on the waiter's poll floor.", channel.Name);
            }
            catch (Exception)
            {
                // A metric listener or log sink that throws costs this wake its diagnostics, never its caller's
                // follow-up: a cancel walks its descendants after this returns.
            }
        }
    }

    /// <summary>
    /// Stable low-cardinality tag values are not derived from enum display text, so an enum rename
    /// cannot silently rename an operator-facing metric dimension. Job-completion channels omit the
    /// namespace tag because a per-job value would explode tag cardinality.
    /// </summary>
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

    internal static string WaitResultTag(WorkerWakeupWaitStatus result) =>
        result switch
        {
            WorkerWakeupWaitStatus.Signaled => "signaled",
            WorkerWakeupWaitStatus.TimedOut => "timed_out",
            _ => "unknown",
        };
}
