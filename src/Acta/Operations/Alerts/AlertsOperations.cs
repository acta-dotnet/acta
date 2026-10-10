namespace Acta;

/// <summary>The Alerts operations: <see cref="IAlerts"/> for applications, plus the engine's calls.</summary>
internal sealed class AlertsOperations(
    AcknowledgeAlertHandler acknowledgeAlert,
    ResolveAlertHandler resolveAlert,
    ListAlertsHandler listAlerts,
    GetAlertHandler getAlert,
    RaiseAlertHandler raiseAlert,
    GetAlertableEventsHandler getAlertableEvents,
    GetDeliverableAlertsHandler getDeliverableAlerts,
    UpdateAlertDeliveryHandler updateAlertDelivery,
    ResolveAlertsHandler resolveAlerts
) : IAlerts
{
    public ValueTask<AcknowledgeAlertResponse> AcknowledgeAsync(
        AlertRef alertRef,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    ) => acknowledgeAlert.HandleAsync(new AcknowledgeAlertCommand(alertRef, reasonMessage, actor), ct);

    public ValueTask<ResolveAlertResponse> ResolveAsync(
        AlertRef alertRef,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    ) => resolveAlert.HandleAsync(new ResolveAlertCommand(alertRef, reasonMessage, actor), ct);

    public ValueTask<ListAlertsResponse> ListAsync(ListAlertsQuery query, CancellationToken ct = default) =>
        listAlerts.HandleAsync(query, ct);

    public ValueTask<GetAlertResponse?> GetAsync(AlertRef alertRef, CancellationToken ct = default) =>
        getAlert.HandleAsync(new GetAlertQuery(alertRef), ct);

    /// <summary>
    /// Persists one alert and returns its post-raise occurrence. A null deduplication key always inserts; a
    /// key names an incident holding at most one open row, which a repeat increments while it is open and a
    /// resolution closes, so the next raise opens a fresh row with a fresh ref. A raise never undoes a
    /// resolution, and a replayed <c>SourceEventId</c> neither inflates a count nor reopens an incident. An
    /// unknown job id throws <see cref="ArgumentException"/>.
    /// </summary>
    internal Task<RaiseAlertResponse> RaiseAlertAsync(RaiseAlertCommand command, CancellationToken ct) =>
        raiseAlert.HandleAsync(command, ct);

    /// <summary>
    /// How far behind the database clock the alertable-event read stops, so every alertable row stamped at or
    /// before that instant has committed; the caller sees it as latency between an event landing and the
    /// pass that projects it, and as the instant a short batch may checkpoint to.
    /// </summary>
    internal TimeSpan SafeHorizonLag => getAlertableEvents.SafeHorizonLag;

    /// <summary>
    /// Reads the namespace's alert-relevant <c>events</c> rows past the <c>sys.alerts</c> cursor and stamped
    /// at or before <paramref name="horizonUtc"/>, ordered by (created_at_utc, id); the cursor is that same
    /// pair. Stamps, not ids, order the walk: the stamp is set inside the writing transaction and the id at
    /// insert, so a slow writer commits a low stamp under a high id that an id-only cursor would skip.
    /// </summary>
    internal Task<GetAlertableEventsResponse> GetAlertableEventsAsync(
        int namespaceId,
        DateTime cursorUtc,
        long cursorEventId,
        int batchSize,
        DateTime horizonUtc,
        CancellationToken ct
    ) => getAlertableEvents.HandleAsync(new GetAlertableEventsQuery(namespaceId, cursorUtc, cursorEventId, batchSize, horizonUtc), ct);

    /// <summary>
    /// Reads the namespace's unresolved alerts due for delivery, both arms keyed off <c>retry_after_utc</c>:
    /// Pending and RetryAfter once it has elapsed or is unset, Delivered and Failed once the reminder their
    /// settlement scheduled is due; Suppressed is never reminded. Not <c>modified_at_utc</c>: every repeat
    /// re-stamps it, so a job failing faster than the reminder interval would never be reminded. Resolved
    /// rows are never sent; an attempt already in flight may still complete.
    /// </summary>
    internal Task<GetDeliverableAlertsResponse> GetDeliverableAlertsAsync(int namespaceId, int batchSize, CancellationToken ct) =>
        getDeliverableAlerts.HandleAsync(new GetDeliverableAlertsQuery(namespaceId, batchSize), ct);

    /// <summary>
    /// Records one delivery attempt's outcome (status, retry count, and <c>retry_after_utc</c>: the next retry
    /// on a retryable failure, the next reminder on a settled one), compare-and-swap on the version delivery
    /// selected. False means the row moved meanwhile (resolved, or settled by another worker); the newer
    /// state stands. A re-fire after resolution is a different row, so it never loses this race.
    /// </summary>
    internal Task<UpdateAlertDeliveryResponse> UpdateAlertDeliveryAsync(
        long alertId,
        int expectedVersion,
        AlertDeliveryStatusCode status,
        int retryCount,
        DateTime? retryAfterUtc,
        CancellationToken ct
    ) => updateAlertDelivery.HandleAsync(new UpdateAlertDeliveryCommand(alertId, expectedVersion, status, retryCount, retryAfterUtc), ct);

    /// <summary>
    /// Resolves one job's open automatic failure alerts whose projection mark precedes
    /// <paramref name="sourceEventId"/>, the success event driving it, and returns how many it closed; a
    /// replay behind a newer failure, or a second success, closes nothing. Closing settles delivery: Pending
    /// and RetryAfter become Suppressed and <c>retry_after_utc</c> is cleared, while a Delivered, Failed, or
    /// Suppressed row keeps the status that records what happened.
    /// </summary>
    internal Task<ResolveAlertsResponse> ResolveAlertsAsync(int namespaceId, long jobId, long sourceEventId, CancellationToken ct) =>
        resolveAlerts.HandleAsync(new ResolveAlertsCommand(namespaceId, jobId, sourceEventId), ct);
}
