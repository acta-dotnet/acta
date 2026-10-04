namespace Acta.Runtime;

/// <summary><see cref="IAlerts"/>, one line per operation.</summary>
internal sealed class Alerts(
    AcknowledgeJobAlertHandler acknowledgeJobAlert,
    ResolveJobAlertManualHandler resolveJobAlertManual,
    ListAlertsHandler listAlerts,
    GetAlertHandler getAlert
) : IAlerts
{
    public ValueTask<AcknowledgeJobAlertResponse> AcknowledgeAsync(
        AlertRef alertRef,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    ) => acknowledgeJobAlert.HandleAsync(new AcknowledgeJobAlertCommand(alertRef, reasonMessage, actorKey), ct);

    public ValueTask<ResolveJobAlertManualResponse> ResolveAsync(
        AlertRef alertRef,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    ) => resolveJobAlertManual.HandleAsync(new ResolveJobAlertManualCommand(alertRef, reasonMessage, actorKey), ct);

    public ValueTask<PagedResult<AlertListItem>> ListAsync(ListAlertsQuery query, CancellationToken ct = default) =>
        listAlerts.HandleAsync(query, ct);

    public ValueTask<AlertDetail?> GetAsync(AlertRef alertRef, CancellationToken ct = default) =>
        getAlert.HandleAsync(new GetAlertQuery(alertRef), ct);
}
