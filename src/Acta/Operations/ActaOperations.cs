namespace Acta;

/// <summary>
/// <see cref="IActaOperations"/> implementation: composes the group facades. Each is registered with its
/// handlers and injected here, so this class is pure composition.
/// </summary>
internal sealed class ActaOperations(
    SelectedProvider provider,
    ISchedules schedules,
    ICalendars calendars,
    IDefinitions definitions,
    IWorkers workers,
    IAlerts alerts,
    ITenants tenants,
    INamespaces namespaces,
    ITags tags,
    IOutbox outbox,
    ILedger ledger
) : IActaOperations
{
    public ISchedules Schedules => schedules;
    public ICalendars Calendars => calendars;
    public IDefinitions Definitions => definitions;
    public IWorkers Workers => workers;
    public IAlerts Alerts => alerts;
    public ITenants Tenants => tenants;
    public INamespaces Namespaces => namespaces;
    public ITags Tags => tags;
    public IOutbox Outbox => outbox;

    public ILedger Ledger => ledger;

    public DbProvider Provider => provider.Provider;
}
