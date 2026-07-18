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

/// <summary>
/// Property-only composite of Acta's internal store ports. Grows one property per migrated feature;
/// it never carries methods and is not a service locator - normal runtime classes inject the
/// narrowest store they need, and receiving the composite requires a genuinely cross-feature
/// responsibility.
/// </summary>
internal interface IActaStore
{
    IOverviewStore Overview { get; }

    IEventStore Events { get; }

    IJobStore Jobs { get; }

    ISignalStore Signals { get; }

    IScheduleStore Schedules { get; }

    IAlertStore Alerts { get; }

    IExecutionStore Execution { get; }

    IRetentionStore Retention { get; }

    IWorkerStore Workers { get; }

    IDefinitionStore Definitions { get; }

    INamespaceStore Namespaces { get; }

    ITenantStore Tenants { get; }
}
