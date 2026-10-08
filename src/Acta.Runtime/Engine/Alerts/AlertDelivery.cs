using Microsoft.Extensions.Logging;

namespace Acta.Runtime;

/// <summary>
/// The deliver phase of <c>sys.alerts</c>: for each row due in the firing namespace it resolves the channel and its
/// transport, sends or suppresses, and settles the row against the version it carried at selection on the pass's
/// settlement clock, at least once, so a crash after the send may resend. One batch per invocation, never a drain:
/// ten thousand webhooks in one tick would turn protecting the database into an outage on the operator's own channel.
/// </summary>
internal sealed class AlertDelivery(
    AlertLifecycle alerts,
    AlertChannelRegistry channels,
    IAlertTransportRegistry transports,
    int maxDeliveryRetries,
    TimeSpan reminderInterval,
    ILogger log
)
{
    private const int DeliverBatchSize = 256;
    private readonly AlertLifecycle _alerts = alerts;
    private readonly AlertChannelRegistry _channels = channels;
    private readonly IAlertTransportRegistry _transports = transports;
    private readonly int _maxDeliveryRetries = maxDeliveryRetries;
    private readonly TimeSpan _reminderInterval = reminderInterval;
    private readonly ILogger _log = log;

    /// <summary>
    /// Delivery retry curve, independent of any job's backoff policy. 30s to 1h, doubling, 10%
    /// jitter (the ranged-expression defaults) - parsed once from the same DSL every definition uses.
    /// </summary>
    private static readonly Backoff RetryBackoff = Backoff.Parse("30s..1h");

    internal async Task DeliverAsync(JobContext ctx, AlertSettlementClock settlement, CancellationToken ct)
    {
        var due = await _alerts.GetDeliverableAlertsAsync(ctx.NamespaceId, DeliverBatchSize, ct);
        foreach (var a in due)
        {
            var channel = _channels.Resolve(ctx.JobNamespace, a.ChannelName);
            var transport = channel is null ? null : _transports.Resolve(channel.TransportKind);
            var decision = AlertChannelVerdict.Decide(a, channel, transport);

            switch (decision.Kind)
            {
                case AlertChannelVerdictKind.Failed:
                    LogFailedDecision(ctx, a, channel, decision.Reason);
                    await SettleAsync(a, AlertSendStatus.Permanent, settlement, ct);
                    continue;

                case AlertChannelVerdictKind.Suppressed:
                    LogSuppressedDecision(ctx, a, channel!, decision.Reason);
                    await SuppressAsync(a, ct);
                    continue;
            }

            var outcome = await SendAsync(ctx, a, channel!, transport!, ct);
            await SettleAsync(a, outcome, settlement, ct);
        }
    }

    private async Task<AlertSendStatus> SendAsync(
        JobContext ctx,
        DeliverableAlert a,
        AlertChannelDeclaration channel,
        IAlertTransport transport,
        CancellationToken ct
    )
    {
        var notification = new AlertNotification(
            new AlertRef(a.AlertRef),
            ctx.JobNamespace,
            a.JobRef is { } jobRef ? new JobRef(jobRef) : null,
            a.Severity,
            a.Kind,
            a.Title,
            a.Message,
            a.RunbookUrl,
            a.OccurrenceCount,
            a.CreatedAtUtc
        );
        var target = new AlertTarget(
            channel.Name,
            channel.TransportKind,
            channel.Endpoint,
            ConfigFormatId: 0,
            Config: ReadOnlyMemory<byte>.Empty
        );

        try
        {
            return await transport.SendAsync(notification, target, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "ACTA sys.alerts: transport kind ({Detail}) threw delivering alert ({Ref}); will retry.",
                channel.TransportKind,
                AlertsJob.RenderRef(a.AlertRef)
            );
            return AlertSendStatus.Retryable;
        }
    }

    private void LogFailedDecision(
        JobContext ctx,
        DeliverableAlert alert,
        AlertChannelDeclaration? channel,
        AlertChannelVerdictReason reason
    )
    {
        if (reason == AlertChannelVerdictReason.MissingChannel)
        {
            _log.LogWarning(
                "ACTA sys.alerts: channel ({Detail}) is not configured for namespace ({Namespace}); marking alert ({Ref}) failed.",
                alert.ChannelName,
                ctx.JobNamespace,
                AlertsJob.RenderRef(alert.AlertRef)
            );
            return;
        }

        _log.LogWarning(
            "ACTA sys.alerts: no transport is registered for ({Detail}); marking alert ({Ref}) delivery failed.",
            $"kind '{channel!.TransportKind}' on channel '{channel.Name}'",
            AlertsJob.RenderRef(alert.AlertRef)
        );
    }

    private void LogSuppressedDecision(
        JobContext ctx,
        DeliverableAlert alert,
        AlertChannelDeclaration channel,
        AlertChannelVerdictReason reason
    )
    {
        if (reason is AlertChannelVerdictReason.DisabledChannel or AlertChannelVerdictReason.DeprecatedChannel)
        {
            _log.LogInformation(
                "ACTA sys.alerts: alert ({Ref}) in namespace ({Namespace}) is ({Outcome}): channel ({Detail}) is ({Reason}).",
                AlertsJob.RenderRef(alert.AlertRef),
                ctx.JobNamespace,
                "Suppressed",
                channel.Name,
                channel.Status.ToString()
            );
            return;
        }

        _log.LogInformation(
            "ACTA sys.alerts: alert ({Ref}) is ({Outcome}): ({Detail}).",
            AlertsJob.RenderRef(alert.AlertRef),
            "Suppressed",
            $"severity {alert.Severity} is below the {channel.MinSeverity} minimum on channel '{channel.Name}'"
        );
    }

    private Task SuppressAsync(DeliverableAlert a, CancellationToken ct) =>
        WriteSettlementAsync(a, AlertDeliveryStatusCode.Suppressed, a.RetryCount, retryAfterUtc: null, ct);

    private Task SettleAsync(DeliverableAlert a, AlertSendStatus outcome, AlertSettlementClock settlement, CancellationToken ct)
    {
        // Read once per settlement, so the status, the count and the instant this row lands with all
        // describe the same moment - the moment the attempt settled, not the moment the pass began.
        var nowUtc = settlement.UtcNow;
        switch (outcome)
        {
            // retry_count back to zero, because it is the budget for one send series and this series just
            // ended. A row that took four attempts to land would otherwise carry that four into its next
            // reminder, where one throw is enough to reach the cap - a reminder that goes terminal on its
            // first failure, having never once been retried. Each series starts with the whole curve.
            case AlertSendStatus.Delivered:
                return WriteSettlementAsync(a, AlertDeliveryStatusCode.Delivered, retryCount: 0, ReminderAfter(a, nowUtc), ct);

            case AlertSendStatus.Retryable:
                var nextRetryCount = a.RetryCount + 1;
                if (nextRetryCount >= _maxDeliveryRetries)
                {
                    // Out of retries, but the incident is still open, so the row keeps a reminder instant:
                    // a transport that was down must not be the reason nobody hears about the outage
                    // again. True for a manual alert too - that send genuinely failed. The count stays at
                    // the cap on purpose: a Failed row's reminder gets one attempt per interval, because
                    // the retry curve belonged to the original send series and the transport already
                    // proved it needs more than a curve.
                    return WriteSettlementAsync(a, AlertDeliveryStatusCode.Failed, nextRetryCount, nowUtc + _reminderInterval, ct);
                }
                var delaySeconds = BackoffSchedule.ComputeDelaySeconds(nextRetryCount, RetryBackoff);
                return WriteSettlementAsync(a, AlertDeliveryStatusCode.RetryAfter, nextRetryCount, nowUtc.AddSeconds(delaySeconds), ct);

            // Permanent: no channel, or no transport for its kind. Same reasoning as the exhausted arm -
            // the send failed, so the row stays on the reminder cadence until the incident closes.
            default:
                return WriteSettlementAsync(a, AlertDeliveryStatusCode.Failed, a.RetryCount, nowUtc + _reminderInterval, ct);
        }
    }

    /// <summary>
    /// When a delivered send should be repeated, written into retry_after_utc as the row's one
    /// "not before" instant. Automatic alerts track a condition Acta watches, so while the incident
    /// stays open the operator is re-notified on the interval. A manual alert is one handler's
    /// statement at one moment: nothing in Acta knows whether it still holds, and the caller owns
    /// resolving it, so turning every ctx.AlertAsync into an unbounded daily nag would be Acta
    /// inventing a lifecycle it cannot see.
    /// </summary>
    private DateTime? ReminderAfter(DeliverableAlert a, DateTime nowUtc) =>
        a.Origin == AlertOriginCode.Automatic ? nowUtc + _reminderInterval : null;

    /// <summary>
    /// Every settlement writes against the version the row carried at selection, and losing that compare-and-swap is
    /// correct: the row moved on and the newer state stands, settled by an operator resolve or a competing worker. The
    /// frequent partner is the raise path's collapse arm, which bumps the version when a repeat of the same condition
    /// lands inside the send window, so this settle loses and the row keeps the state the read found (Pending, an
    /// elapsed RetryAfter, or a settled row past its reminder), which the next pass selects again: a re-send, allowed
    /// at least once, carrying the count the repeat wrote. No retry and no warning, only Debug for tracing one attempt.
    /// </summary>
    private async Task WriteSettlementAsync(
        DeliverableAlert a,
        AlertDeliveryStatusCode status,
        int retryCount,
        DateTime? retryAfterUtc,
        CancellationToken ct
    )
    {
        if (!await _alerts.UpdateAlertDeliveryAsync(a.AlertId, a.Version, status, retryCount, retryAfterUtc, ct))
        {
            _log.LogDebug(
                "ACTA sys.alerts: alert ({Ref}) moved while its delivery attempt was in flight; settlement is ({Outcome}) ({Detail}).",
                AlertsJob.RenderRef(a.AlertRef),
                "Skipped",
                $"expected version {a.Version}"
            );
        }
    }
}
