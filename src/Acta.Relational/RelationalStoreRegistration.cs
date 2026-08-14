using Acta.Relational.Stores;
using Acta.Runtime.Maintenance;
using Acta.Runtime.Modules.Alerting;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Definitions;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Runtime.Modules.Execution.Namespaces;
using Acta.Runtime.Modules.Execution.Schedules;
using Acta.Runtime.Modules.Execution.Settings;
using Acta.Runtime.Modules.Execution.Signals;
using Acta.Runtime.Modules.Execution.Tenants;
using Acta.Runtime.Modules.Execution.Workers;
using Acta.Runtime.Modules.Operations.Events;
using Acta.Runtime.Modules.Operations.Overview;
using Acta.Runtime.Modules.Operations.Tags;
using Acta.Runtime.Modules.Outbox;
using Acta.Runtime.Services.Locks;
using Acta.Runtime.Services.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Acta.Relational;

/// <summary>
/// The feature-store registrations every relational provider shares: one sync point instead of a
/// copied list per provider. Dialect, session, bootstrap, and the per-assembly SQL resource
/// catalog stay in each provider's registration.
/// </summary>
internal static class RelationalStoreRegistration
{
    internal static void AddActaRelationalStores(this IServiceCollection services)
    {
        services.AddSingleton<IOverviewStore, RelationalOverviewStore>();
        services.AddSingleton<IEventStore, RelationalEventStore>();
        services.AddSingleton<IDefinitionStore, RelationalDefinitionStore>();
        services.AddSingleton<INamespaceStore, RelationalNamespaceStore>();
        services.AddSingleton<ITenantStore, RelationalTenantStore>();
        services.AddSingleton<ISettingStore, RelationalSettingStore>();
        services.AddSingleton<IJobStore, RelationalJobStore>();
        services.AddSingleton<ITagStore, RelationalTagStore>();
        services.AddSingleton<ISignalStore, RelationalSignalStore>();
        services.AddSingleton<IOutboxSignalStore, RelationalOutboxSignalStore>();
        services.AddSingleton<IScheduleStore, RelationalScheduleStore>();
        services.AddSingleton<IWorkerStore, RelationalWorkerStore>();
        services.AddSingleton<IAlertStore, RelationalAlertStore>();
        services.AddSingleton<IRetentionStore, RelationalRetentionStore>();
        services.AddSingleton<IExecutionStore, RelationalExecutionStore>();
        services.TryAddSingleton<ILockStore, RelationalLockStore>();
        services.AddSingleton<IServerClock, RelationalActaClock>();
        services.TryAddSingleton<IActaClock>(sp => sp.GetRequiredService<IServerClock>());
    }
}
