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
/// profile, and collapses automatic alerts onto one open incident row per job and condition.
/// Deterministic poison events are durably recorded on the projector job before its cursor advances;
/// transient failures retain the cursor for retry. Deliver resolves logical channels and transports,
/// then records delivered, suppressed, retryable, or terminal outcomes. Delivery is at least once: a
/// crash after send but before settlement may resend a rare duplicate.
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
    // variable collides. Internal because the crash-replay and poison-event tests stage their
    // failures against this exact checkpoint name; referencing the constant makes a rename a compile
    // error instead of a spec that quietly stops staging its crash.
    internal const string CursorVariableName = "alerts-cursor";
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
        await GenerateAsync(ctx, ct);
        await DeliverAsync(ctx, nowUtc, ct);
    }

    private async Task GenerateAsync(JobContext ctx, CancellationToken ct)
    {
        var cursor = await ctx.GetVariableOrDefaultAsync<long>(CursorVariableName, 0L, ct);

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
                await ProjectAsync(ctx, e, ct);
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
            "ACTA sys.alerts: skipped poison alert event ({Detail}) in namespace ({Namespace}); reason ({Reason}).",
            $"{e.EventId} (durable outcome recorded in job variable '{variableName}')",
            ctx.JobNamespace,
            reason
        );
        _metrics?.RecordAlertProjectionSkip(ctx.JobNamespace, reason);
    }

    // Classify one event and emit the alerts selected by its definition profile. Failure events can
    // fire first-failure, final-failure, or threshold-reached alerts. A success emits nothing: it only
    // closes this job's open automatic failure alerts, keeping the resolved timestamp as the single
    // source of truth for open state.
    private async Task ProjectAsync(JobContext ctx, AlertableEvent e, CancellationToken ct)
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
            // Resolution is job-instance-scoped: a success closes only THIS job's open automatic failure
            // alerts, never a sibling job of the same definition, and writes no alert of its own. Resolve
            // on EVERY success (no per-pass dedup): within one batch a job can go fail -> success -> fail
            // -> success, where the second failure opened a SECOND incident on the same key; only
            // resolving each success keeps it from lingering unresolved. The op is idempotent and a
            // success with nothing open closes nothing, so the repeat costs a no-op. The success event's
            // own id rides along: the store closes only alerts an OLDER event moved, so replaying this
            // success behind a newer failure leaves the alert that failure opened open.
            if (e.JobId is { } jobId)
            {
                await _store.ResolveJobAlertsAsync(ctx.NamespaceId, jobId, e.EventId, ct);
            }
            return;
        }

        if (e.ToStatus == JobStatusCode.Failed)
        {
            var severity = system
                ? AlertSeverityCode.Critical
                : (profile == AlertProfileCode.Info ? AlertSeverityCode.Info : AlertSeverityCode.Error);
            await EmitAsync(ctx, e, channel, AlertKindCode.FinalFailure, severity, ct);
            return;
        }

        // Non-terminal failure (re-armed for retry / next occurrence). Only OnFailure and the system
        // profile alert on these; OnTerminal / Info stay quiet until the terminal transition.
        if (profile is not (AlertProfileCode.OnFailure or AlertProfileCode.SysCritical))
        {
            return;
        }

        var firstSeverity = system ? AlertSeverityCode.Critical : AlertSeverityCode.Warning;
        var raise = await EmitAsync(ctx, e, channel, AlertKindCode.FirstFailure, firstSeverity, ct);
        // Escalate only when THIS event is the one the row just absorbed. An applied raise stamps the
        // incoming id as the row's mark, so on a first pass the extra condition changes nothing. A
        // replay-held raise returns the STORED count - already at the threshold - with a newer mark;
        // without the mark check every replayed event under that count re-fired ThresholdReached,
        // blaming the wrong events and inflating the row. The true crossing event still matches the
        // mark on replay, and the ThresholdReached row's own high-water guard makes that re-emit a
        // no-op when the pre-crash emit landed - or a correct single-source recovery when it did not.
        if (raise.OccurrenceCount == _failureThreshold && raise.LastProjectedEventId == e.EventId)
        {
            await EmitAsync(
                ctx,
                e,
                channel,
                AlertKindCode.ThresholdReached,
                system ? AlertSeverityCode.Critical : AlertSeverityCode.Error,
                ct
            );
        }
    }

    private async Task<AlertRaiseOutcome> EmitAsync(
        JobContext ctx,
        AlertableEvent e,
        string channel,
        AlertKindCode reason,
        AlertSeverityCode severity,
        CancellationToken ct
    )
    {
        // Job-instance-scoped incident identity: includes the job id so a fan-out of sibling jobs of the same
        // definition each get their own row (and a success resolves only that job's failures), while
        // repeated failures of the SAME job still collapse onto its one open row.
        var jobReason = e.ReasonCode?.Code;
        var deduplicationKey = $"auto:{e.DefinitionId}:{e.JobId}:{reason.Code}:{jobReason ?? "none"}";

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
                // The projecting event's id: the store increments and re-stamps only when it is newer
                // than the row's mark, and refuses to open an incident behind a mark this identity
                // already carries, so re-projecting a batch after a crash changes nothing.
                e.EventId
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

    // Encodes the render-don't-pass-typed rule for log sites: a serializing sink must never see the raw uuid.
    private static string RenderRef(Guid alertRef) => new AlertRef(alertRef).ToString();

    private static (string Title, string Message) Render(AlertableEvent e, AlertKindCode reason)
    {
        var reasonText = e.ReasonMessage ?? e.ReasonCode?.Code ?? "no reason recorded";
        return reason switch
        {
            AlertKindCode.FinalFailure => ($"Job '{e.JobName}' failed", $"Terminal failure: {reasonText}."),
            AlertKindCode.FirstFailure => ($"Job '{e.JobName}' attempt failed", $"Attempt failed: {reasonText}. Retrying."),
            AlertKindCode.ThresholdReached => (
                $"Job '{e.JobName}' failing repeatedly",
                $"Repeated failures since this incident opened: {reasonText}."
            ),
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
                "ACTA sys.alerts: transport kind ({Detail}) threw delivering alert ({Ref}); will retry.",
                channel.TransportKind,
                RenderRef(a.AlertRef)
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
                "ACTA sys.alerts: channel ({Detail}) is not configured for namespace ({Namespace}); marking alert ({Ref}) failed.",
                alert.ChannelName,
                ctx.JobNamespace,
                RenderRef(alert.AlertRef)
            );
            return;
        }

        _log.LogWarning(
            "ACTA sys.alerts: no transport is registered for ({Detail}); marking alert ({Ref}) delivery failed.",
            $"kind '{channel!.TransportKind}' on channel '{channel.Name}'",
            RenderRef(alert.AlertRef)
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
                "ACTA sys.alerts: alert ({Ref}) in namespace ({Namespace}) is ({Outcome}): channel ({Detail}) is ({Reason}).",
                RenderRef(alert.AlertRef),
                ctx.JobNamespace,
                "Suppressed",
                channel.Name,
                channel.Status.ToString()
            );
            return;
        }

        _log.LogInformation(
            "ACTA sys.alerts: alert ({Ref}) is ({Outcome}): ({Detail}).",
            RenderRef(alert.AlertRef),
            "Suppressed",
            $"severity {alert.Severity} is below the {channel.MinSeverity} minimum on channel '{channel.Name}'"
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
