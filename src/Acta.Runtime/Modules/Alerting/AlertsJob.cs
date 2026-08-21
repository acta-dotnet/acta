using System.Diagnostics;
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
/// profile, and collapses automatic alerts onto one open incident row per job and condition. It drains
/// in bounded batches within one invocation, checkpointing its cursor after each, so a backlog clears
/// in a tick rather than one batch per minute.
/// Deterministic poison events are durably recorded on the projector job before its cursor advances;
/// transient failures retain the cursor for retry. Deliver resolves logical channels and transports,
/// then records delivered, suppressed, retryable, or terminal outcomes against the version the row
/// carried at selection. Delivery is at least once: a crash after send but before settlement may
/// resend a rare duplicate.
///
/// <para>An alert resolved before delivery selection is not sent. Resolution suppresses further pending
/// and retry attempts. A transport attempt already in progress may still complete.</para>
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

    // Internal for the same reason as the cursor name: the retention sweep prunes these by prefix and
    // its spec stages them against this exact one, so a rename is a compile error rather than a spec
    // that quietly stops staging what it prunes.
    internal const string SkipVariablePrefix = "alerts-skip-";
    private const string DefaultChannelName = "default";
    private const int GenerateBatchSize = 256;
    private const int DeliverBatchSize = 256;

    // The generate drain's two bounds, and the reason each one is where it is. 40 batches of 256 is
    // 10,240 events in one invocation: a burst clears in a tick instead of the ~40 minutes that one
    // batch per minute took, while the pass still has a ceiling rather than scanning an uncapped
    // backlog. The 30s budget is soft and checked BETWEEN batches, so the batch in flight always
    // finishes and always checkpoints; it makes a long pass yield on its own terms well inside the
    // framework's 300s execution timeout, which sys.alerts inherits and which stays the backstop.
    // Delivery is deliberately NOT drained this way and stays at DeliverBatchSize per invocation:
    // pushing 10,000 webhooks through one tick would turn protecting the database into an outage on
    // the operator's own channel.
    private const int GenerateMaxBatches = 40;
    private static readonly TimeSpan GenerateTimeBudget = TimeSpan.FromSeconds(30);

    // Delivery retry curve, independent of any job's backoff policy. 30s to 1h, doubling, 10% jitter
    // (the ranged-expression defaults) - parsed once from the same DSL every definition uses.
    private static readonly Backoff RetryBackoff = Backoff.Parse("30s..1h");

    private readonly IAlertStore _store = store;
    private readonly IActaClock _clock = clock;
    private readonly IAlertChannelRegistry _channels = channels;
    private readonly IAlertTransportRegistry _transports = transports;
    private readonly int _maxDeliveryRetries = options.Value.AlertDeliveryMaxRetries;
    private readonly int _failureThreshold = options.Value.AlertFailureThreshold;
    private readonly TimeSpan _reminderInterval = options.Value.AlertReminderInterval;
    private readonly ILogger _log = log ?? NullLogger<AlertsJob>.Instance;
    private readonly JobMetrics? _metrics = metrics;

    // The shipped drain bounds as one value. Internal because the drain specs stage backlogs relative
    // to the real batch size rather than to a literal 256, so raising the constant keeps those specs
    // spanning batches instead of quietly collapsing into single-batch passes.
    internal static readonly AlertDrainBudget DefaultDrain = new(GenerateBatchSize, GenerateMaxBatches, GenerateTimeBudget);

    /// <summary>
    /// The bounds the generate drain runs under, defaulting to <see cref="DefaultDrain"/>. Init-only
    /// and internal so the drain specs can reach the batch cap and spend the time budget without
    /// staging ten thousand events or timing a wall clock; production never assigns it.
    /// </summary>
    internal AlertDrainBudget Drain { get; init; } = DefaultDrain;

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
        // The database clock is read once, here, and every settlement below stamps itself from that
        // instant plus the time the pass has spent since - see AlertSettlementClock for why a pass-start
        // instant alone is the wrong base once a pass can legally run for tens of seconds.
        var settlement = AlertSettlementClock.Start(await _clock.GetUtcNowAsync(ct));
        await GenerateAsync(ctx, ct);
        await DeliverAsync(ctx, settlement, ct);
    }

    // One bounded drain per invocation: batches until the backlog runs out, the batch cap is reached,
    // or the elapsed budget is spent - whichever comes first. The cursor is written after every
    // completed batch rather than once at the end, so whatever ends the pass - a bound, a crash, the
    // execution timeout - keeps every batch already projected and the next invocation resumes behind
    // it. Re-offering the one batch that was in flight is safe by construction: the raise and resolve
    // paths refuse to move an incident an equal-or-newer event already marked.
    //
    // The cursor below is the highest id the READ returned, which is only a safe checkpoint because the
    // store's read is horizon-bounded: it withholds events too recent for every transaction that could
    // still commit a lower id to have finished. Without that bound this fold would step over an id whose
    // transaction had not committed yet, and nothing would ever read that event again. Any change here
    // that reads events from a source other than IAlertStore.GetAlertableEventsAsync reopens that.
    private async Task GenerateAsync(JobContext ctx, CancellationToken ct)
    {
        var cursor = await ctx.GetVariableOrDefaultAsync<long>(CursorVariableName, 0L, ct);
        // Monotonic, because this is a local cooperative budget rather than a correctness instant: it
        // decides only whether THIS pass keeps going, so it must not move with the database's clock.
        var elapsed = Stopwatch.StartNew();
        var projected = 0;

        for (var batch = 0; batch < Drain.MaxBatches; batch++)
        {
            var events = await _store.GetAlertableEventsAsync(ctx.NamespaceId, cursor, Drain.BatchSize, ct);
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

            projected += events.Count;
            if (maxId > cursor)
            {
                await ctx.SetVariableAsync(CursorVariableName, maxId, ct);
                cursor = maxId;
            }

            // A short read is the end of what this pass can have: the query takes everything above the
            // cursor and behind the horizon, up to the limit, so asking again would come back empty.
            // Events newer than the horizon are not a backlog this pass is behind on - they are not
            // eligible yet, and the next pass picks them up once they age past it.
            if (events.Count < Drain.BatchSize)
            {
                return;
            }

            if (elapsed.Elapsed >= Drain.TimeBudget)
            {
                LogDrainBound(ctx, "time-budget", projected, elapsed);
                return;
            }
        }

        LogDrainBound(ctx, "batch-cap", projected, elapsed);
    }

    // Information, not Warning: reaching a bound is the design working. The line an operator wants is
    // the one that explains why a backlog is clearing a tick at a time, so it names which bound ended
    // the pass and how much the pass got through.
    private void LogDrainBound(JobContext ctx, string reason, int projected, Stopwatch elapsed) =>
        _log.LogInformation(
            "ACTA sys.alerts: the generate drain stopped at its ({Reason}) bound in namespace ({Namespace}) having projected {Count} events in {DurationMs} ms; the next pass resumes from the cursor.",
            reason,
            ctx.JobNamespace,
            projected,
            elapsed.ElapsedMilliseconds
        );

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

        // Non-terminal failure (re-armed for retry, for the next occurrence, or back onto a wait whose
        // timeout it had already resolved when its worker died). Only OnFailure and the system
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

    private async Task DeliverAsync(JobContext ctx, AlertSettlementClock settlement, CancellationToken ct)
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
                    await SettleAsync(a, AlertDeliveryOutcome.Permanent, settlement, ct);
                    continue;

                case AlertChannelDecisionKind.Suppressed:
                    LogSuppressedDecision(ctx, a, channel!, decision.Reason);
                    await SuppressAsync(a, ct);
                    continue;
            }

            var outcome = await SendAsync(ctx, a, channel!, transport!, ct);
            await SettleAsync(a, outcome, settlement, ct);
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
        WriteSettlementAsync(a, AlertDeliveryStatusCode.Suppressed, a.RetryCount, retryAfterUtc: null, ct);

    private Task SettleAsync(DeliverableAlert a, AlertDeliveryOutcome outcome, AlertSettlementClock settlement, CancellationToken ct)
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
            case AlertDeliveryOutcome.Delivered:
                return WriteSettlementAsync(a, AlertDeliveryStatusCode.Delivered, retryCount: 0, ReminderAfter(a, nowUtc), ct);

            case AlertDeliveryOutcome.Retryable:
                var nextRetryCount = (byte)Math.Min(a.RetryCount + 1, byte.MaxValue);
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

    // When a delivered send should be repeated, written into retry_after_utc as the row's one "not
    // before" instant. Automatic alerts track a condition Acta watches, so while the incident stays open
    // the operator is re-notified on the interval. A manual alert is one handler's statement at one
    // moment: nothing in Acta knows whether it still holds, and the caller owns resolving it, so turning
    // every ctx.AlertAsync into an unbounded daily nag would be Acta inventing a lifecycle it cannot see.
    private DateTime? ReminderAfter(DeliverableAlert a, DateTime nowUtc) =>
        a.Origin == AlertOriginCode.Automatic ? nowUtc + _reminderInterval : null;

    // Every settlement writes against the version the row carried when this pass selected it. Losing
    // that compare-and-swap is a correct outcome, not an error: the row moved on and the newer state is
    // the one that should stand. Three partners move it. An operator resolve, and a competing worker's
    // settlement of the same attempt, both leave the row settled, so the lost write changes nothing that
    // matters. The most frequent one is quieter: the raise path's collapse arm bumps version too, so a
    // repeat of the same condition - a ctx.AlertAsync landing inside the send window - makes this settle
    // lose, and the row keeps the state the read found it in - Pending, RetryAfter with an instant
    // already elapsed, or a settled row whose reminder instant has passed - each of which the next
    // pass selects again. That is a re-send on the next pass, which
    // delivery is allowed (at least once) and which is the better answer anyway: the re-send carries the
    // occurrence count the repeat just wrote. So there is no retry and no warning here; the next pass
    // re-selects whatever is genuinely due. Debug, because the only reader who wants this line is someone
    // tracing why one attempt left no trace.
    private async Task WriteSettlementAsync(
        DeliverableAlert a,
        AlertDeliveryStatusCode status,
        byte retryCount,
        DateTime? retryAfterUtc,
        CancellationToken ct
    )
    {
        if (!await _store.UpdateAlertDeliveryAsync(a.AlertId, a.Version, status, retryCount, retryAfterUtc, ct))
        {
            _log.LogDebug(
                "ACTA sys.alerts: alert ({Ref}) moved while its delivery attempt was in flight; settlement is ({Outcome}) ({Detail}).",
                RenderRef(a.AlertRef),
                "Skipped",
                $"expected version {a.Version}"
            );
        }
    }
}

/// <summary>
/// The bounds one <c>sys.alerts</c> generate pass drains within: how many events one batch reads
/// (<c>BatchSize</c>), how many batches the pass may complete (<c>MaxBatches</c>), and the elapsed
/// budget it spends (<c>TimeBudget</c>). The time budget is cooperative - read between batches, never
/// inside one - so the batch in flight always finishes and always checkpoints its cursor. The first
/// bound reached ends the pass.
/// </summary>
internal sealed record AlertDrainBudget(int BatchSize, int MaxBatches, TimeSpan TimeBudget);

/// <summary>
/// The instant one <c>sys.alerts</c> pass stamps a settlement with: the database clock read once at the
/// start of the pass, advanced by the monotonic time spent since that read.
///
/// <para>A pass is long enough for the difference to matter. The generate drain may spend a 30-second
/// budget before delivery starts, and delivery then adds one transport round trip per row. Computing
/// <c>retry_after_utc</c> from the pass-start instant alone therefore stamps a backoff or a reminder
/// that part of the pass has already consumed, and a short backoff can land in the past outright -
/// which makes the next pass re-select the row immediately and collapses the spacing the curve
/// promises.</para>
///
/// <para>Monotonic rather than a second clock read per settlement: it costs no database round trip on
/// the delivery path, and it cannot move backwards if the server's wall clock is adjusted mid-pass.
/// The base instant stays the database's, so settlements remain comparable with every other stored
/// instant; only the offset is local.</para>
/// </summary>
internal readonly struct AlertSettlementClock(DateTime baseUtc, long startedTimestamp)
{
    /// <summary>Starts a pass clock from <paramref name="baseUtc"/>, read from the database just now.</summary>
    public static AlertSettlementClock Start(DateTime baseUtc) => new(baseUtc, Stopwatch.GetTimestamp());

    /// <summary>The base instant plus the elapsed time since it was read.</summary>
    public DateTime UtcNow => baseUtc + Stopwatch.GetElapsedTime(startedTimestamp);
}
