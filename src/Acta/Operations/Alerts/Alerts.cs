namespace Acta;

/// <summary><see cref="IAlerts"/>, one line per operation.</summary>
internal sealed class Alerts(
    AcknowledgeAlertHandler acknowledgeAlert,
    ResolveAlertHandler resolveAlert,
    ListAlertsHandler listAlerts,
    GetAlertHandler getAlert
) : IAlerts
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
}
