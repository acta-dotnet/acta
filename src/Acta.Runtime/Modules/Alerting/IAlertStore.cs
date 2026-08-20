using Acta.Runtime.Kernel;
using Acta.Runtime.Modules.Execution.Api;

namespace Acta.Runtime.Modules.Alerting;

/// <summary>
/// Persistence port for job alerts: the incident-identity upsert raise, the <c>sys.alerts</c> generate
/// and deliver phase reads, the delivery-outcome update, the success auto-resolve, the operator
/// acknowledge/resolve verbs, and the paged operator list.
/// </summary>
internal interface IAlertStore
{
    /// <summary>
    /// Persists one <c>alerts</c> row and returns its post-upsert <c>occurrence_count</c> and
    /// <c>last_projected_event_id</c>. A null deduplication key always inserts (count 1, the command's
    /// <c>SourceEventId</c> as the mark); a non-null key names an incident identity
    /// <c>(namespace_id, deduplication_key)</c> holding at most one OPEN row, so a repeat increments
    /// that row while it is unresolved and opens a fresh one - fresh ref, count 1, fresh delivery - once
    /// it is resolved. A raise never undoes a resolution.
    ///
    /// <para>A <c>SourceEventId</c> increments and re-stamps the open row only when it is newer than the
    /// row's mark, and opens a new row only when no row of the identity is already marked at or past it,
    /// so a replayed failure neither inflates a count nor resurrects a closed incident. A held write
    /// returns the identity's newest row, already marked at this event or a newer one: that pair can
    /// re-assert the escalation this event earned - the crash-recovery case the caller's threshold emit
    /// is idempotent about - but never invent one for a neighbour. A null <c>SourceEventId</c> always
    /// applies; an unknown job id throws <see cref="ArgumentException"/>.</para>
    /// </summary>
    Task<AlertRaiseOutcome> RaiseJobAlertAsync(RaiseJobAlertCommand command, CancellationToken ct);

    /// <summary>
    /// Reads the namespace's alert-relevant <c>events</c> rows above the <c>sys.alerts</c> cursor,
    /// ordered by the monotonic event id so the caller resumes from the last id it consumed.
    /// </summary>
    Task<IReadOnlyList<AlertableEvent>> GetAlertableEventsAsync(short namespaceId, long cursorEventId, int batchSize, CancellationToken ct);

    /// <summary>
    /// Reads the namespace's unresolved alerts due for delivery. Both arms key off
    /// <c>retry_after_utc</c>, the row's one "not before" instant: <c>{Pending, RetryAfter}</c> when it
    /// has elapsed or is unset, and <c>{Delivered, Failed}</c> when the reminder their settlement
    /// scheduled has elapsed - so a failed send does not silence an open incident, while
    /// <c>Suppressed</c> is never reminded (a routing decision, not a failed send).
    ///
    /// <para>Deliberately not <c>modified_at_utc</c>: every repeat the open incident absorbs re-stamps
    /// that column, so a job failing faster than the reminder interval would be permanently too young
    /// to remind - the outage that most needs re-notifying.</para>
    ///
    /// <para>Resolved rows are excluded from both arms: an alert resolved before delivery selection is
    /// not sent. Resolution suppresses further pending and retry attempts. A transport attempt already
    /// in progress may still complete.</para>
    /// </summary>
    Task<IReadOnlyList<DeliverableAlert>> GetDeliverableAlertsAsync(short namespaceId, int batchSize, CancellationToken ct);

    /// <summary>
    /// Records the outcome of one alert delivery attempt: sets <c>delivery_status_code</c>,
    /// <c>retry_count</c>, and <c>retry_after_utc</c> - which carries the next retry instant on a
    /// retryable failure and the next reminder instant on a settled one. Compare-and-swap
    /// on <paramref name="expectedVersion"/> - the version the row carried when delivery selected it -
    /// and returns whether the swap applied. A miss means the row moved while the attempt was in
    /// flight (an operator resolved it, or another worker settled it); the newer state stands and the
    /// caller writes nothing. A post-resolution re-fire is a different row with its own id, so it is
    /// never the loser of this race.
    /// </summary>
    Task<bool> UpdateAlertDeliveryAsync(
        long alertId,
        int expectedVersion,
        AlertDeliveryStatusCode status,
        byte retryCount,
        DateTime? retryAfterUtc,
        CancellationToken ct
    );

    /// <summary>
    /// Marks one job's open automatic failure alerts resolved and returns the number of rows closed,
    /// stamping <paramref name="sourceEventId"/> - the id of the success event driving the resolution -
    /// on every row it closes. Only alerts whose <c>last_projected_event_id</c> precedes that id are
    /// closed, so replaying a success behind a newer failure leaves the alert that failure opened alone.
    /// Idempotent: a second success closes nothing.
    ///
    /// <para>Closing a row also settles its delivery: a <c>Pending</c> or <c>RetryAfter</c> row becomes
    /// <c>Suppressed</c> and every closed row's <c>retry_after_utc</c> is cleared, so the recovery
    /// cancels the notification instead of leaving it queued. A row already <c>Delivered</c>,
    /// <c>Failed</c>, or <c>Suppressed</c> keeps that status: it records what actually happened.</para>
    /// </summary>
    Task<int> ResolveJobAlertsAsync(short namespaceId, long jobId, long sourceEventId, CancellationToken ct);

    /// <summary>
    /// Acknowledge one alert: missing row is NotFound; an already-acknowledged row is Applied without
    /// mutation; else stamps <c>acknowledged_at_utc</c> and emits <c>alert.acknowledged</c>.
    /// </summary>
    Task<AlertControlOutcome> AcknowledgeJobAlertAsync(AlertControlCommand command, CancellationToken ct);

    /// <summary>
    /// Manually resolve one alert: missing row is NotFound; an already-resolved row is Applied without
    /// mutation; else stamps <c>resolved_at_utc</c> and emits <c>alert.resolved</c>. Does not require a
    /// prior acknowledge. Settles delivery exactly as the automatic resolve does: <c>Pending</c> and
    /// <c>RetryAfter</c> become <c>Suppressed</c>, <c>retry_after_utc</c> is cleared, and an already
    /// settled status stands.
    /// </summary>
    Task<AlertControlOutcome> ResolveJobAlertManualAsync(AlertControlCommand command, CancellationToken ct);

    /// <summary>
    /// One keyset page of <c>alerts</c> rows ordered <c>created_at_utc DESC, id DESC</c> plus the
    /// opt-in filter-wide total, fetched in one round trip as two result sets.
    /// </summary>
    Task<AlertPage> ListJobAlertsAsync(AlertPageRequest request, CancellationToken ct);

    /// <summary>Point-read of one alert row by ref, in the list projection's shape; null when missing.</summary>
    Task<AlertListItem?> GetJobAlertAsync(Guid alertRef, CancellationToken ct);
}

/// <summary>
/// Validated alert raise; construct via <see cref="Create"/> so channel canonicalization, deduplication-key
/// normalization, and bounded-prose truncation happen once, identically for every caller.
/// </summary>
internal sealed record RaiseJobAlertCommand(
    string JobNamespace,
    long? JobId,
    AlertOriginCode Origin,
    AlertSeverityCode Severity,
    AlertKindCode Kind,
    string Title,
    string Message,
    string ChannelName,
    AlertDeliveryStatusCode DeliveryStatus,
    string? DeduplicationKey,
    long? SourceEventId,
    Guid AlertRef
)
{
    public static RaiseJobAlertCommand Create(
        string jobNamespace,
        long? jobId,
        AlertOriginCode origin,
        AlertSeverityCode severity,
        AlertKindCode kind,
        string title,
        string message,
        string channelName,
        AlertDeliveryStatusCode deliveryStatus,
        string? deduplicationKey,
        long? sourceEventId
    )
    {
        channelName = IdentifierSyntax.CanonicalizeKebab(channelName, nameof(channelName), ActaTextLimits.AlertChannelName);
        if (deduplicationKey is not null)
        {
            deduplicationKey = IdentifierSyntax.NormalizeKey(deduplicationKey, nameof(deduplicationKey), ActaTextLimits.AlertDedupeKey);
        }

        return new RaiseJobAlertCommand(
            jobNamespace,
            jobId,
            origin,
            severity,
            kind,
            title.Truncate(ActaTextLimits.AlertTitle)!,
            message.Truncate(ActaTextLimits.AlertMessage)!,
            channelName,
            deliveryStatus,
            deduplicationKey.Truncate(ActaTextLimits.AlertDedupeKey),
            sourceEventId,
            // The candidate public ref is minted here so every caller carries one; the raise consumes it
            // only when the upsert actually inserts, so one incident keeps one ref across every repeat it
            // absorbs and the next incident on the same key gets a new one.
            Acta.AlertRef.New().Value
        );
    }
}

/// <summary>Validated operator control verb target: the alert ref plus the audit actor and reason.</summary>
internal sealed record AlertControlCommand(Guid AlertRef, JobControlActor Actor, string ReasonMessage);

/// <summary>Decoded alerts list request; <c>Take</c> carries the page-size-plus-one peek-ahead.</summary>
internal sealed record AlertPageRequest(
    string? JobNamespace,
    long? JobId,
    bool? UnresolvedOnly,
    AlertSeverityCode? SeverityAtLeast,
    AlertDeliveryStatusCode? DeliveryStatus,
    bool? Acknowledged,
    DateTime? CursorCreatedAtUtc,
    long? CursorId,
    int Take,
    bool IncludeTotal,
    string? TagFiltersJson = null
);

/// <summary>One page of mapped alert list items plus the opt-in filtered total.</summary>
internal sealed record AlertPage(IReadOnlyList<AlertListItem> Rows, long? Total);
