using Acta.Features.Alerts;
using Acta.Features.Definitions;
using Acta.Features.Events;
using Acta.Features.Execution;
using Acta.Features.Jobs;
using Acta.Features.Namespaces;
using Acta.Features.Overview;
using Acta.Features.Retention;
using Acta.Features.Schedules;
using Acta.Features.Signals;
using Acta.Features.Tenants;
using Acta.Features.Workers;

namespace Acta;

/// <summary>DI-composed <see cref="IActaStore"/>: one property per provider-implemented store port.</summary>
internal sealed class ActaStore(
    IOverviewStore overview,
    IEventStore events,
    IJobStore jobs,
    ISignalStore signals,
    IScheduleStore schedules,
    IAlertStore alerts,
    IExecutionStore execution,
    IRetentionStore retention,
    IWorkerStore workers,
    IDefinitionStore definitions,
    INamespaceStore namespaces,
    ITenantStore tenants
) : IActaStore
{
    public IOverviewStore Overview { get; } = overview;

    public IEventStore Events { get; } = events;

    public IJobStore Jobs { get; } = jobs;

    public ISignalStore Signals { get; } = signals;

    public IScheduleStore Schedules { get; } = schedules;

    public IAlertStore Alerts { get; } = alerts;

    public IExecutionStore Execution { get; } = execution;

    public IRetentionStore Retention { get; } = retention;

    public IWorkerStore Workers { get; } = workers;

    public IDefinitionStore Definitions { get; } = definitions;

    public INamespaceStore Namespaces { get; } = namespaces;

    public ITenantStore Tenants { get; } = tenants;
}
