using Acta.Runtime.Modules.Execution.Api;

namespace Acta.Runtime.Modules.Alerting;

/// <summary>
/// Implements execution's <see cref="IAlertSink"/> over the alert store, keeping alert policy on
/// the alerting side of the seam: the manual origin/kind codes, the <c>default</c> channel
/// fallback, and the incident identity (a null deduplication key always inserts; a non-null key
/// collapses onto the one open row carrying it, and opens a fresh row once that one is resolved).
/// </summary>
internal sealed class AlertStoreSink(IAlertStore store) : IAlertSink
{
    public Task RaiseManualAsync(
        string jobNamespace,
        long jobId,
        AlertSeverityCode severityCode,
        string title,
        string message,
        string? channelName,
        string? deduplicationKey,
        CancellationToken ct
    ) =>
        store.RaiseJobAlertAsync(
            RaiseJobAlertCommand.Create(
                jobNamespace,
                jobId,
                AlertOriginCode.Manual,
                severityCode,
                AlertKindCode.Manual,
                title,
                message,
                channelName ?? "default",
                AlertDeliveryStatusCode.Pending,
                deduplicationKey,
                // No projected event behind a manual raise: it always applies and never moves the
                // projection high-water mark.
                sourceEventId: null
            ),
            ct
        );
}
