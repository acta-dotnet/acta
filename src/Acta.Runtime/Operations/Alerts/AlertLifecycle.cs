namespace Acta.Runtime;

/// <summary><see cref="IAlertLifecycle"/>, one line per operation.</summary>
internal sealed class AlertLifecycle(
    RaiseJobAlertHandler raiseJobAlert,
    GetAlertableEventsHandler getAlertableEvents,
    GetDeliverableAlertsHandler getDeliverableAlerts,
    UpdateAlertDeliveryHandler updateAlertDelivery,
    ResolveJobAlertsHandler resolveJobAlerts
) : IAlertLifecycle
{
    public Task<AlertOccurrence> RaiseJobAlertAsync(RaiseJobAlertCommand command, CancellationToken ct) =>
        raiseJobAlert.HandleAsync(command, ct);

    public TimeSpan SafeHorizonLag => getAlertableEvents.SafeHorizonLag;

    public Task<IReadOnlyList<AlertableEvent>> GetAlertableEventsAsync(
        int namespaceId,
        DateTime cursorUtc,
        long cursorEventId,
        int batchSize,
        DateTime horizonUtc,
        CancellationToken ct
    ) => getAlertableEvents.HandleAsync(new GetAlertableEventsQuery(namespaceId, cursorUtc, cursorEventId, batchSize, horizonUtc), ct);

    public Task<IReadOnlyList<DeliverableAlert>> GetDeliverableAlertsAsync(int namespaceId, int batchSize, CancellationToken ct) =>
        getDeliverableAlerts.HandleAsync(new GetDeliverableAlertsQuery(namespaceId, batchSize), ct);

    public Task<bool> UpdateAlertDeliveryAsync(
        long alertId,
        int expectedVersion,
        AlertDeliveryStatusCode status,
        int retryCount,
        DateTime? retryAfterUtc,
        CancellationToken ct
    ) => updateAlertDelivery.HandleAsync(new UpdateAlertDeliveryCommand(alertId, expectedVersion, status, retryCount, retryAfterUtc), ct);

    public Task<int> ResolveJobAlertsAsync(int namespaceId, long jobId, long sourceEventId, CancellationToken ct) =>
        resolveJobAlerts.HandleAsync(new ResolveJobAlertsCommand(namespaceId, jobId, sourceEventId), ct);
}
