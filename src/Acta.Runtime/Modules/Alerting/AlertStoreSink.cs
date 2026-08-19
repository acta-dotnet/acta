using Acta.Runtime.Modules.Execution.Api;
using Acta.Runtime.Services.Time;
using Microsoft.Extensions.Options;

namespace Acta.Runtime.Modules.Alerting;

/// <summary>
/// Implements execution's <see cref="IAlertSink"/> over the alert store, keeping alert policy on
/// the alerting side of the seam: the manual origin/kind codes, the <c>default</c> channel
/// fallback, and the dedupe-window bucketing (a null deduplication key always inserts; a non-null
/// key buckets UTC now to a multiple of <see cref="JobsOptions.AlertDedupeWindow"/> so repeats
/// inside the window land on the same row).
/// </summary>
internal sealed class AlertStoreSink(IAlertStore store, IActaClock clock, IOptions<JobsOptions> options) : IAlertSink
{
    public async Task RaiseManualAsync(
        string jobNamespace,
        long jobId,
        AlertSeverityCode severityCode,
        string title,
        string message,
        string? channelName,
        string? deduplicationKey,
        CancellationToken ct
    )
    {
        DateTime? windowStart = null;
        if (deduplicationKey is not null)
        {
            var now = await clock.GetUtcNowAsync(ct);
            windowStart = AlertWindow.FloorStart(now, options.Value.AlertDedupeWindow);
        }

        await store.RaiseJobAlertAsync(
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
                windowStart,
                // No projected event behind a manual raise: it always applies and never moves the
                // projection high-water mark.
                sourceEventId: null
            ),
            ct
        );
    }
}
