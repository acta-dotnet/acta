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
        services.AddSingleton<IJobStore, RelationalJobStore>();
        services.AddSingleton<IExecutionStore, RelationalExecutionStore>();
    }
}
