using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Acta;

/// <summary>
/// The deliver phase of <c>sys.alerts</c>: for each row due in the firing namespace it resolves the channel and its
/// transport, sends or suppresses, and settles the row against the version it carried at selection on the pass's
/// settlement clock, at least once, so a crash after the send may resend. One batch per invocation, never a drain:
/// ten thousand webhooks in one tick would turn protecting the database into an outage on the operator's own channel.
/// Each send has a limit, a channel that misses it is not asked again in the pass, and the pass has a budget, so an
/// endpoint that never answers costs a pass one limit however many alerts it has queued.
/// </summary>
internal sealed class AlertDelivery(
    AlertsOperations alerts,
    AlertChannelRegistry channels,
    IAlertTransportRegistry transports,
    int maxDeliveryRetries,
    TimeSpan reminderInterval,
    ILogger log
)
{
    private const int DeliverBatchSize = 256;
    private readonly AlertsOperations _alerts = alerts;
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

    internal async Task DeliverAsync(JobContext ctx, AlertSettlementClock settlement, AlertDeliveryBudget budget, CancellationToken ct)
    {
        var due = (await _alerts.ListDeliverableAlertsAsync(ctx.NamespaceId, DeliverBatchSize, ct)).Alerts;
        var elapsed = Stopwatch.StartNew();
        HashSet<string>? unanswered = null;
        foreach (var a in due)
        {
            // A pass that spent its budget leaves the rest due for the next one, a minute on.
            if (elapsed.Elapsed >= budget.Pass)
            {
                if (_log.IsEnabled(LogLevel.Warning))
                {
                    ActaLog.AlertDeliveryStoppedAtBudget(_log, ctx.JobNamespace, elapsed.ElapsedMilliseconds);
                }

                return;
            }

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

            // A channel that missed its limit once is not asked again this pass: its other alerts stay due, uncharged,
            // and the alerts of every other channel are reached.
            if (unanswered?.Contains(channel!.Name) == true)
            {
                continue;
            }

            var (outcome, answered) = await SendAsync(ctx, a, channel!, transport!, budget.Send, ct);
            if (!answered)
            {
                (unanswered ??= new HashSet<string>(StringComparer.Ordinal)).Add(channel!.Name);
            }

            await SettleAsync(a, outcome, settlement, ct);
        }
    }

    private async Task<(AlertSendStatus Outcome, bool Answered)> SendAsync(
        JobContext ctx,
        DeliverableAlert a,
        AlertChannelDeclaration channel,
        IAlertTransport transport,
        TimeSpan limit,
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

        // A transport is anyone's code. The wait for a send ends at its limit, since a transport that ignores its token
        // would otherwise hold every alert behind it; the pass waits on nothing else of the transport's.
        var send = SendWithinAsync(transport, notification, target, limit, ct);
        try
        {
            return (await send.WaitAsync(limit, ct), Answered: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_log.IsEnabled(LogLevel.Warning))
            {
                ActaLog.AlertTransportThrew(_log, ex, channel.TransportKind, AlertsJob.RenderRef(a.AlertRef));
            }

            // The limit reaches the pass as the wait's timeout or as the send's own cancellation, whichever comes first.
            return (AlertSendStatus.Retryable, Answered: ex is not (TimeoutException or OperationCanceledException));
        }
        finally
        {
            // A send given up at its limit may still fault; that fault is read here, so it is not left unobserved.
            _ = send.ContinueWith(
                static late => _ = late.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );
        }
    }

    /// <summary>
    /// One send under a token its own timer cancels at <paramref name="limit"/>. The token lives as long as the send, so
    /// a send that outlives the wait for it still sees the cancellation, and whatever the transport registered on the
    /// token runs on the timer's thread, never on the pass.
    /// </summary>
    private static async Task<AlertSendStatus> SendWithinAsync(
        IAlertTransport transport,
        AlertNotification notification,
        AlertTarget target,
        TimeSpan limit,
        CancellationToken ct
    )
    {
        using var limited = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limited.CancelAfter(limit);
        return await transport.SendAsync(notification, target, limited.Token).ConfigureAwait(false);
    }

    private void LogFailedDecision(
        JobContext ctx,
        DeliverableAlert alert,
        AlertChannelDeclaration? channel,
        AlertChannelVerdictReason reason
    )
    {
        if (!_log.IsEnabled(LogLevel.Warning))
        {
            return;
        }
        if (reason == AlertChannelVerdictReason.MissingChannel)
        {
            ActaLog.AlertChannelNotConfigured(_log, alert.ChannelName, ctx.JobNamespace, AlertsJob.RenderRef(alert.AlertRef));
            return;
        }

        ActaLog.AlertTransportMissing(
            _log,
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
        if (!_log.IsEnabled(LogLevel.Information))
        {
            return;
        }
        var alertRef = AlertsJob.RenderRef(alert.AlertRef);
        if (reason is AlertChannelVerdictReason.DisabledChannel or AlertChannelVerdictReason.DeprecatedChannel)
        {
            var status = $"{channel.Name} is {channel.Status}";
            ActaLog.AlertSuppressedByChannel(_log, alertRef, ctx.JobNamespace, status);
            return;
        }

        var below = $"severity {alert.Severity} is below the {channel.MinSeverity} minimum on channel '{channel.Name}'";
        ActaLog.AlertSuppressedBySeverity(_log, alertRef, below);
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
    /// "not before" instant. An alert the engine raised tracks a condition Acta watches, so while the
    /// incident stays open the operator is re-notified on the interval. A manual alert is one handler's
    /// statement at one moment: nothing in Acta knows whether it still holds, and the caller owns
    /// resolving it, so turning every ctx.AlertAsync into an unbounded daily nag would be Acta
    /// inventing a lifecycle it cannot see.
    /// </summary>
    private DateTime? ReminderAfter(DeliverableAlert a, DateTime nowUtc) =>
        a.Kind != AlertKindCode.Manual ? nowUtc + _reminderInterval : null;

    /// <summary>
    /// Every settlement writes against the version the row carried at selection, and losing that compare-and-swap is
    /// correct: the row moved on and the newer state stands, settled by an operator resolve or a competing worker. The
    /// frequent partner is the raise path's collapse arm, which bumps the version when a repeat of the same condition
    /// lands inside the send window, so this settle loses and the row keeps the state the read found (Pending, an
    /// elapsed RetryAfter, or a settled row past its reminder), which the next pass selects again: a re-send, allowed
    /// at least once, carrying the count the repeat wrote. No retry and no log line.
    /// </summary>
    private async Task WriteSettlementAsync(
        DeliverableAlert a,
        AlertDeliveryStatusCode status,
        int retryCount,
        DateTime? retryAfterUtc,
        CancellationToken ct
    ) => await _alerts.UpdateAlertDeliveryAsync(a.AlertId, a.Version, status, retryCount, retryAfterUtc, ct);
}
