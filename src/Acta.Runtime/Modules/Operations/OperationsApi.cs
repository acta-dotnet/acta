using Acta.Runtime.Hosting;
using Acta.Runtime.Modules.Execution.Api;
using Acta.Runtime.Modules.Operations.Events;
using Acta.Runtime.Modules.Operations.Overview;

namespace Acta.Runtime.Modules.Operations;

/// <summary>
/// <see cref="IActaOperations"/> implementation: composes the module-owned domain facades, the
/// overview read, and the list reads (jobs through Execution's declared query API, events through
/// the module's own read model). Operations holds no Execution stores; each facade is registered by
/// its owning module and injected here, so this class is pure composition.
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
    OverviewService overview,
    IExecutionQueries executionQueries,
    EventsService events
) : IActaOperations
{
    public ISchedules Schedules => schedules;
    public IDefinitions Definitions => definitions;
    public IWorkers Workers => workers;
    public IAlerts Alerts => alerts;
    public ITenants Tenants => tenants;
    public INamespaces Namespaces => namespaces;
    public ITags Tags => tags;

    public ValueTask<PagedResult<JobListItem>> ListJobsAsync(ListJobsQuery query, CancellationToken ct = default) =>
        executionQueries.ListJobsAsync(query, ct);

    public ValueTask<PagedResult<JobEventListItem>> ListJobEventsAsync(ListJobEventsQuery query, CancellationToken ct = default) =>
        events.ListJobEventsAsync(query, ct);

    public ValueTask<OverviewSnapshot> GetOverviewAsync(OverviewQuery query, CancellationToken ct = default) =>
        overview.GetOverviewAsync(query, ct);

    public DbProvider Provider => provider.Provider;
}
