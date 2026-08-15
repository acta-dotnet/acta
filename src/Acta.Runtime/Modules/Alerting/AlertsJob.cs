using Acta.Runtime.Kernel;
using Acta.Runtime.Modules.Alerting.Api;
using Acta.Runtime.Services.Time;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Acta.Runtime.Modules.Alerting;

/// <summary>
/// Recurring <c>sys.alerts</c> projector and delivery job, competitively claimed once per namespace.
/// Generate classifies alertable events from their immutable transition triple, applies the definition
/// profile, and deduplicates automatic alerts per job and window. Deterministic poison events are
/// durably recorded on the projector job before its cursor advances; transient failures retain the
/// cursor for retry. Deliver resolves logical channels and transports, then records delivered,
/// suppressed, retryable, or terminal outcomes. Delivery is at least once: a crash after send but
/// before settlement may resend a rare duplicate.
/// </summary>
internal sealed class AlertsJob(
    IAlertStore store,
    IActaClock clock,
    IAlertChannelRegistry channels,
    IAlertTransportRegistry transports,
    IOptions<JobsOptions> options,
    ILogger<AlertsJob>? log = null,
    JobMetrics? metrics = null
)
{
    // User-kebab: stored on the sys.alerts slot's own variable bag, so no user
    // variable collides.
    private const string CursorVariableName = "alerts-cursor";
    private const string SkipVariablePrefix = "alerts-skip-";
    private const string DefaultChannelName = "default";
    private const int GenerateBatchSize = 256;
    private const int DeliverBatchSize = 256;

    // Delivery retry curve, independent of any job's backoff policy. 30s to 1h, doubling, 10% jitter
    // (the ranged-expression defaults) - parsed once from the same DSL every definition uses.
    private static readonly Backoff RetryBackoff = Backoff.Parse("30s..1h");

    private readonly IAlertStore _store = store;
    private readonly IActaClock _clock = clock;
    private readonly IAlertChannelRegistry _channels = channels;
    private readonly IAlertTransportRegistry _transports = transports;
    private readonly int _maxDeliveryRetries = options.Value.AlertDeliveryMaxRetries;
    private readonly int _failureThreshold = options.Value.AlertFailureThreshold;
    private readonly TimeSpan _dedupeWindow = options.Value.AlertDedupeWindow;
    private readonly ILogger _log = log ?? NullLogger<AlertsJob>.Instance;
    private readonly JobMetrics? _metrics = metrics;

    /// <summary>
    /// Runs one alerting pass for the firing namespace: projects new alert-relevant events into
    /// <c>alerts</c> rows, then delivers the rows that are due. <c>AuditLevel.Failures</c> keeps idle
    /// ticks out of <c>events</c>.
    /// </summary>
    [Job(
        "sys.alerts",
        Priority = JobPriorityCode.Critical,
        AuditLevel = JobAuditLevelCode.Failures,
        AlertProfile = AlertProfileCode.SysCritical
    )]
    [JobSchedule("default", Cron.EveryMinute)]
    public async Task Handle(JobContext ctx, CancellationToken ct)
    {
        var nowUtc = await _clock.GetUtcNowAsync(ct);
        await GenerateAsync(ctx, nowUtc, ct);
        await DeliverAsync(ctx, nowUtc, ct);
    }

    private async Task GenerateAsync(JobContext ctx, DateTime nowUtc, CancellationToken ct)
    {
        var cursor = await ctx.GetVariableOrDefaultAsync<long>(CursorVariableName, 0L, ct);
        var windowStart = AlertWindow.FloorStart(nowUtc, _dedupeWindow);

        var events = await _store.GetAlertableEventsAsync(ctx.NamespaceId, cursor, GenerateBatchSize, ct);
        if (events.Count == 0)
        {
            return;
        }

        var maxId = cursor;
        foreach (var e in events)
        {
            maxId = Math.Max(maxId, e.EventId);
            try
            {
                await ProjectAsync(ctx, e, windowStart, ct);
            }
            catch (AlertProjectionDataException ex)
            {
                await RecordProjectionSkipAsync(ctx, e, ex, ct);
            }
        }

        if (maxId > cursor)
        {
            await ctx.SetVariableAsync(CursorVariableName, maxId, ct);
        }
    }

    private async Task RecordProjectionSkipAsync(
        JobContext ctx,
        AlertableEvent e,
        AlertProjectionDataException exception,
        CancellationToken ct
    )
    {
        var reason = exception.Reason;
        var variableName = SkipVariablePrefix + e.EventId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var detail = exception.Message.Truncate(ActaTextLimits.ReasonMessage);
        var durableRecord =
            $"namespace={ctx.JobNamespace};eventId={e.EventId};reason={reason};exception={exception.GetType().Name};detail={detail}";

        // Persist before moving the shared cursor. If this write fails, the whole pass fails and the event
        // is retried. A crash after this write but before the cursor update safely overwrites the same slot.
        await ctx.SetVariableAsync(variableName, durableRecord, ct);

        _log.LogWarning(
            exception,
            "ACTA sys.alerts: skipped poison event {EventId} in namespace {Namespace}; reason {SkipReason}. Durable outcome: {SkipVariable}.",
            e.EventId,
            ctx.JobNamespace,
            reason,
            variableName
        );
        _metrics?.RecordAlertProjectionSkip(ctx.JobNamespace, reason);
    }

    // Classify one event and emit the alerts selected by its definition profile. Failure events can
    // fire first-failure, final-failure, or threshold-reached alerts. A success closes the definition's
    // open automatic failure alerts and emits one recovery alert when it actually resolved something,
    // keeping the resolved timestamp as the single source of truth for open state.
    private async Task ProjectAsync(JobContext ctx, AlertableEvent e, DateTime windowStart, CancellationToken ct)
    {
        var profile = e.AlertProfile;
        if (profile == AlertProfileCode.None)
        {
            return;
        }

        // SysCritical only raises the severity to Critical; the channel is uniform (the declared
        // one, else the configured "default" log channel), so system-job failures sink to logs out of the box.
        var system = profile == AlertProfileCode.SysCritical;
        var channel = e.AlertChannelName ?? DefaultChannelName;

        var isSuccess = e.ExecutionStatus == ExecutionStatusCode.Succeeded;
        if (isSuccess)
        {
            // Recovery is job-instance-scoped: a success resolves only THIS job's open automatic failure
            // alerts, never a sibling job of the same definition. Resolve on EVERY success (no per-pass
            // dedup): within one batch a job can go fail -> success -> fail -> success, where the second
            // failure re-opened (resolved_at = NULL) the deduped alert; only resolving each success keeps it
            // from lingering unresolved. The op is idempotent and a no-op success closes nothing, so the
            // duplicate recovery alerts collapse to one row via the deduplication key.
            if (e.JobId is { } jobId)
            {
                var resolved = await _store.ResolveJobAlertsAsync(ctx.NamespaceId, jobId, ct);
                if (resolved > 0)
                {
                    await EmitAsync(ctx, e, channel, AlertKindCode.Recovery, AlertSeverityCode.Info, windowStart, ct);
                }
            }
            return;
        }

        if (e.ToStatus == JobStatusCode.Failed)
        {
            var severity = system
                ? AlertSeverityCode.Critical
                : (profile == AlertProfileCode.Info ? AlertSeverityCode.Info : AlertSeverityCode.Error);
            await EmitAsync(ctx, e, channel, AlertKindCode.FinalFailure, severity, windowStart, ct);
            return;
        }

        // Non-terminal failure (re-armed for retry / next occurrence). Only OnFailure and the system
        // profile alert on these; OnTerminal / Info stay quiet until the terminal transition.
        if (profile is not (AlertProfileCode.OnFailure or AlertProfileCode.SysCritical))
        {
            return;
        }

        var firstSeverity = system ? AlertSeverityCode.Critical : AlertSeverityCode.Warning;
        var occurrence = await EmitAsync(ctx, e, channel, AlertKindCode.FirstFailure, firstSeverity, windowStart, ct);
        if (occurrence == _failureThreshold)
        {
            await EmitAsync(
                ctx,
                e,
                channel,
                AlertKindCode.ThresholdReached,
                system ? AlertSeverityCode.Critical : AlertSeverityCode.Error,
                windowStart,
                ct
            );
        }
    }

    private async Task<int> EmitAsync(
        JobContext ctx,
        AlertableEvent e,
        string channel,
        AlertKindCode reason,
        AlertSeverityCode severity,
        DateTime windowStart,
        CancellationToken ct
    )
    {
        // Job-instance-scoped deduplication key: includes the job id so a fan-out of sibling jobs of the same
        // definition each get their own row (and recovery resolves only that job's failures), while
        // repeated failures of the SAME job still collapse onto one row.
        var jobReason = e.ReasonCode?.Code;
        var deduplicationKey =
            reason == AlertKindCode.Recovery
                ? $"auto:{e.DefinitionId}:{e.JobId}:{reason.Code}"
                : $"auto:{e.DefinitionId}:{e.JobId}:{reason.Code}:{jobReason ?? "none"}";

        var (title, message) = Render(e, reason);

        // The two proven poison shapes are re-tagged here, at the site that proves them, so the
        // projector's skip path never swallows an ArgumentException raised by an unrelated defect.
        RaiseJobAlertCommand command;
        try
        {
            command = RaiseJobAlertCommand.Create(
                ctx.JobNamespace,
                e.JobId,
                AlertOriginCode.Automatic,
                severity,
                reason,
                title,
                message,
                channel,
                AlertDeliveryStatusCode.Pending,
                deduplicationKey,
                windowStart
            );
        }
        catch (ArgumentException ex)
        {
            // A stored field (channel name, deduplication key) failed canonicalization: the event
            // itself is malformed, and retrying can never fix it.
            throw new AlertProjectionDataException("invalid-event", ex.Message, ex);
        }

        try
        {
            return await _store.RaiseJobAlertAsync(command, ct);
        }
        catch (ArgumentException ex) when (string.Equals(ex.ParamName, "jobId", StringComparison.Ordinal))
        {
            // The provider's ACTA:ALERT_UNKNOWN_JOB signal: the subject job row is gone (purged
            // between the event write and this projection), so the event can never project.
            throw new AlertProjectionDataException("unknown-job", ex.Message, ex);
        }
    }

    private static (string Title, string Message) Render(AlertableEvent e, AlertKindCode reason)
    {
        var reasonText = e.ReasonMessage ?? e.ReasonCode?.Code ?? "no reason recorded";
        return reason switch
        {
            AlertKindCode.FinalFailure => ($"Job '{e.JobName}' failed", $"Terminal failure: {reasonText}."),
            AlertKindCode.FirstFailure => ($"Job '{e.JobName}' attempt failed", $"Attempt failed: {reasonText}. Retrying."),
            AlertKindCode.ThresholdReached => (
                $"Job '{e.JobName}' failing repeatedly",
                $"Repeated failures within the alert window: {reasonText}."
            ),
            AlertKindCode.Recovery => ($"Job '{e.JobName}' recovered", "A previously-failing job completed successfully."),
            _ => ($"Job '{e.JobName}'", reasonText),
        };
    }

    private async Task DeliverAsync(JobContext ctx, DateTime nowUtc, CancellationToken ct)
    {
        var due = await _store.GetDeliverableAlertsAsync(ctx.NamespaceId, DeliverBatchSize, ct);
        foreach (var a in due)
        {
            var channel = _channels.Resolve(ctx.JobNamespace, a.ChannelName);
            var transport = channel is null ? null : _transports.Resolve(channel.TransportKind);
            var decision = AlertChannelDecision.Decide(a, channel, transport);

            switch (decision.Kind)
            {
                case AlertChannelDecisionKind.Failed:
                    LogFailedDecision(ctx, a, channel, decision.Reason);
                    await SettleAsync(a, AlertDeliveryOutcome.Permanent, nowUtc, ct);
                    continue;

                case AlertChannelDecisionKind.Suppressed:
                    LogSuppressedDecision(ctx, a, channel!, decision.Reason);
                    await SuppressAsync(a, ct);
                    continue;
            }

            var outcome = await SendAsync(ctx, a, channel!, transport!, ct);
            await SettleAsync(a, outcome, nowUtc, ct);
        }
    }

    private async Task<AlertDeliveryOutcome> SendAsync(
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
                "ACTA sys.alerts: transport '{TransportKind}' threw delivering alert {AlertId}; will retry.",
                channel.TransportKind,
                a.AlertId
            );
            return AlertDeliveryOutcome.Retryable;
        }
    }

    private void LogFailedDecision(
        JobContext ctx,
        DeliverableAlert alert,
        AlertChannelDeclaration? channel,
        AlertChannelDecisionReason reason
    )
    {
        if (reason == AlertChannelDecisionReason.MissingChannel)
        {
            _log.LogWarning(
                "ACTA sys.alerts: channel '{Channel}' is not configured for namespace '{Namespace}'; marking alert {AlertId} failed.",
                alert.ChannelName,
                ctx.JobNamespace,
                alert.AlertId
            );
            return;
        }

        _log.LogWarning(
            "ACTA sys.alerts: no transport registered for kind '{TransportKind}' (channel '{Channel}', alert {AlertId}); marking delivery failed.",
            channel!.TransportKind,
            channel.Name,
            alert.AlertId
        );
    }

    private void LogSuppressedDecision(
        JobContext ctx,
        DeliverableAlert alert,
        AlertChannelDeclaration channel,
        AlertChannelDecisionReason reason
    )
    {
        if (reason is AlertChannelDecisionReason.DisabledChannel or AlertChannelDecisionReason.DeprecatedChannel)
        {
            _log.LogInformation(
                "ACTA sys.alerts: channel '{Channel}' is {Status} for namespace '{Namespace}'; suppressing alert {AlertId}.",
                channel.Name,
                channel.Status,
                ctx.JobNamespace,
                alert.AlertId
            );
            return;
        }

        _log.LogInformation(
            "ACTA sys.alerts: alert {AlertId} severity {Severity} is below min severity {MinSeverity} for channel '{Channel}'; suppressing delivery.",
            alert.AlertId,
            alert.Severity,
            channel.MinSeverity,
            channel.Name
        );
    }

    private Task SuppressAsync(DeliverableAlert a, CancellationToken ct) =>
        _store.UpdateAlertDeliveryAsync(a.AlertId, AlertDeliveryStatusCode.Suppressed, a.RetryCount, retryAfterUtc: null, ct);

    private Task SettleAsync(DeliverableAlert a, AlertDeliveryOutcome outcome, DateTime nowUtc, CancellationToken ct)
    {
        switch (outcome)
        {
            case AlertDeliveryOutcome.Delivered:
                return _store.UpdateAlertDeliveryAsync(a.AlertId, AlertDeliveryStatusCode.Delivered, a.RetryCount, retryAfterUtc: null, ct);

            case AlertDeliveryOutcome.Retryable:
                var nextRetryCount = (byte)Math.Min(a.RetryCount + 1, byte.MaxValue);
                if (nextRetryCount >= _maxDeliveryRetries)
                {
                    return _store.UpdateAlertDeliveryAsync(
                        a.AlertId,
                        AlertDeliveryStatusCode.Failed,
                        nextRetryCount,
                        retryAfterUtc: null,
                        ct
                    );
                }
                var delaySeconds = BackoffSchedule.ComputeDelaySeconds(nextRetryCount, RetryBackoff);
                return _store.UpdateAlertDeliveryAsync(
                    a.AlertId,
                    AlertDeliveryStatusCode.RetryAfter,
                    nextRetryCount,
                    nowUtc.AddSeconds(delaySeconds),
                    ct
                );

            default:
                return _store.UpdateAlertDeliveryAsync(a.AlertId, AlertDeliveryStatusCode.Failed, a.RetryCount, retryAfterUtc: null, ct);
        }
    }
}
