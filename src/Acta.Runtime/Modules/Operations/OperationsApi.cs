using Acta.Modules.Operations.Overview;

namespace Acta.Modules.Operations;

/// <summary>
/// <see cref="IActaOperations"/> implementation: composes the module-owned domain facades and the
/// overview read. Operations holds no stores of its own; each facade is registered by its owning
/// module and injected here, so this class is pure composition.
/// </summary>
internal sealed class OperationsApi(
    ActaProviderInfo provider,
    ISchedules schedules,
    IDefinitions definitions,
    IWorkers workers,
    IAlerts alerts,
    ITenants tenants,
    INamespaces namespaces,
    ITags tags,
    OverviewService overview
) : IActaOperations
{
    public ISchedules Schedules => schedules;
    public IDefinitions Definitions => definitions;
    public IWorkers Workers => workers;
    public IAlerts Alerts => alerts;
    public ITenants Tenants => tenants;
    public INamespaces Namespaces => namespaces;
    public ITags Tags => tags;

    public ValueTask<OverviewSnapshot> GetOverviewAsync(OverviewQuery query, CancellationToken ct = default) =>
        overview.GetOverviewAsync(query, ct);

    public DbProvider Provider => provider.Provider;
}
