using Acta.Runtime.Kernel;
using Acta.Runtime.Modules.Execution.Api;

namespace Acta.Runtime.Modules.Alerting;

/// <summary>
/// Persistence port for job alerts: the dedupe-window upsert raise, the <c>sys.alerts</c> generate
/// and deliver phase reads, the delivery-outcome update, the success auto-resolve, the operator
/// acknowledge/resolve verbs, and the paged operator list.
/// </summary>
internal interface IAlertStore
{
    /// <summary>
    /// Persists one <c>alerts</c> row and returns its post-upsert <c>occurrence_count</c>. A null
    /// deduplication key always inserts (returns 1); a non-null key upserts on
    /// <c>(namespace_id, deduplication_key, dedupe_window_start_utc)</c>, collapsing repeats inside the
    /// window onto one row. A command carrying a <c>SourceEventId</c> only increments, re-opens, and
    /// re-stamps the row when that id is newer than the row's <c>last_projected_event_id</c>; a replay of
    /// an already-projected event changes nothing and returns the stored count. A null
    /// <c>SourceEventId</c> (a manual raise) always applies. Throws <see cref="ArgumentException"/> when
    /// the referenced job id does not exist.
    /// </summary>
    Task<int> RaiseJobAlertAsync(RaiseJobAlertCommand command, CancellationToken ct);

    /// <summary>
    /// Reads the namespace's alert-relevant <c>events</c> rows above the <c>sys.alerts</c> cursor,
    /// ordered by the monotonic event id so the caller resumes from the last id it consumed.
    /// </summary>
    Task<IReadOnlyList<AlertableEvent>> GetAlertableEventsAsync(short namespaceId, long cursorEventId, int batchSize, CancellationToken ct);

    /// <summary>
    /// Reads the namespace's alerts due for delivery: <c>delivery_status</c> in
    /// <c>{Pending, RetryAfter}</c> with <c>retry_after_utc</c> elapsed (or unset).
    /// </summary>
    Task<IReadOnlyList<DeliverableAlert>> GetDeliverableAlertsAsync(short namespaceId, int batchSize, CancellationToken ct);

    /// <summary>
    /// Records the outcome of one alert delivery attempt: sets <c>delivery_status_code</c> and, on a
    /// retryable failure, bumps <c>retry_count</c> and stamps <c>retry_after_utc</c>.
    /// </summary>
    Task UpdateAlertDeliveryAsync(
        long alertId,
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
    /// prior acknowledge.
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
    DateTime? DedupeWindowStartUtc,
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
        DateTime? dedupeWindowStartUtc,
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
            dedupeWindowStartUtc,
            sourceEventId,
            // The candidate public ref is minted here so every caller carries one; the raise consumes it
            // only when the upsert actually inserts, leaving a deduped row's ref stable across re-fires.
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
