using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Acta.Runtime;

/// <summary>
/// The feature-store registrations every relational provider shares: one sync point instead of a
/// copied list per provider. Dialect, session, bootstrap, and the per-assembly SQL resource
/// catalog stay in each provider's registration.
/// </summary>
internal static class RelationalStoreRegistration
{
    internal static void AddActaRelationalStores(this IServiceCollection services)
    {
        services.AddSingleton<RetentionCoordinator>();
        services.AddSingleton<IOverviewStore, RelationalOverviewStore>();
        services.AddSingleton<IEventStore, RelationalEventStore>();
        services.AddSingleton<IDefinitionStore, RelationalDefinitionStore>();
        services.AddSingleton<INamespaceStore, RelationalNamespaceStore>();
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
    }
}
