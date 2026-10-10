namespace Acta;

/// <summary><see cref="IAlertsOperations"/>, one line per operation.</summary>
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
) : IAlertsOperations
{
    public ValueTask<AcknowledgeAlertResponse> AcknowledgeAsync(
        AlertRef alertRef,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    ) => acknowledgeAlert.HandleAsync(new AcknowledgeAlertCommand(alertRef, reasonMessage, actorKey), ct);

    public ValueTask<ResolveAlertResponse> ResolveAsync(
        AlertRef alertRef,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    ) => resolveAlert.HandleAsync(new ResolveAlertCommand(alertRef, reasonMessage, actorKey), ct);

    public ValueTask<ListAlertsResponse> ListAsync(ListAlertsQuery query, CancellationToken ct = default) =>
        listAlerts.HandleAsync(query, ct);

    public ValueTask<GetAlertResponse?> GetAsync(AlertRef alertRef, CancellationToken ct = default) =>
        getAlert.HandleAsync(new GetAlertQuery(alertRef), ct);

    public Task<RaiseAlertResponse> RaiseAlertAsync(RaiseAlertCommand command, CancellationToken ct) => raiseAlert.HandleAsync(command, ct);

    public TimeSpan SafeHorizonLag => getAlertableEvents.SafeHorizonLag;

    public async Task<IReadOnlyList<AlertableEvent>> GetAlertableEventsAsync(
        int namespaceId,
        DateTime cursorUtc,
        long cursorEventId,
        int batchSize,
        DateTime horizonUtc,
        CancellationToken ct
    ) =>
        (
            await getAlertableEvents.HandleAsync(
                new GetAlertableEventsQuery(namespaceId, cursorUtc, cursorEventId, batchSize, horizonUtc),
                ct
            )
        ).Events;

    public async Task<IReadOnlyList<DeliverableAlert>> GetDeliverableAlertsAsync(int namespaceId, int batchSize, CancellationToken ct) =>
        (await getDeliverableAlerts.HandleAsync(new GetDeliverableAlertsQuery(namespaceId, batchSize), ct)).Alerts;

    public Task<UpdateAlertDeliveryResponse> UpdateAlertDeliveryAsync(
        long alertId,
        int expectedVersion,
        AlertDeliveryStatusCode status,
        int retryCount,
        DateTime? retryAfterUtc,
        CancellationToken ct
    ) => updateAlertDelivery.HandleAsync(new UpdateAlertDeliveryCommand(alertId, expectedVersion, status, retryCount, retryAfterUtc), ct);

    public Task<ResolveAlertsResponse> ResolveAlertsAsync(int namespaceId, long jobId, long sourceEventId, CancellationToken ct) =>
        resolveAlerts.HandleAsync(new ResolveAlertsCommand(namespaceId, jobId, sourceEventId), ct);
}
