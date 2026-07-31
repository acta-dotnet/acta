using Acta.Postgres.Configuration;
using Acta.Postgres.Hosting;
using Acta.Postgres.Services;
using Acta.Relational.Commands;
using Acta.Relational.Connections;
using Acta.Relational.Resources;
using Acta.Relational.Stores;
using Acta.Runtime.Hosting;
using Acta.Runtime.Maintenance;
using Acta.Runtime.Modules.Alerting;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Definitions;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Runtime.Modules.Execution.Namespaces;
using Acta.Runtime.Modules.Execution.Schedules;
using Acta.Runtime.Modules.Execution.Signals;
using Acta.Runtime.Modules.Execution.Tenants;
using Acta.Runtime.Modules.Execution.Workers;
using Acta.Runtime.Modules.Operations.Events;
using Acta.Runtime.Modules.Operations.Overview;
using Acta.Runtime.Modules.Operations.Tags;
using Acta.Runtime.Services.Locks;
using Acta.Runtime.Services.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Acta.Postgres;

/// <summary>
/// PostgreSQL provider registration. Core owns feature behavior; this package supplies the complete
/// provider store set, command binding, executable SQL, and relational mechanics.
/// </summary>
public static class PostgresActaBuilderExtensions
{
    /// <summary>
    /// Selects PostgreSQL as Acta's durable provider. Registers connection string/schema
    /// options, relational mechanics, provider bootstrap, and provider-owned feature stores.
    /// </summary>
    public static IActaBuilder UsePostgres(this IActaBuilder builder, Action<PostgresProviderOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        ActaProviderRegistration.Add(builder.Services, new ActaProviderInfo(DbProvider.Postgres, SupportsRoutines: true));
        builder.Services.Configure(configure);
        builder.Services.AddOptions<PostgresProviderOptions>().ValidateOnStart();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<PostgresProviderOptions>, SqlProviderOptionsValidator<PostgresProviderOptions>>()
        );

        // The dialect owns generic connection, parameter, routine, and transaction traits. Feature
        // stores below own their commands, executable SQL, binding, and projections directly.
        builder.Services.AddSingleton<SqlProviderOptions>(static sp => sp.GetRequiredService<IOptions<PostgresProviderOptions>>().Value);
        builder.Services.AddSingleton<PostgresDialect>();
        builder.Services.AddSingleton<ISqlDialect>(static sp => sp.GetRequiredService<PostgresDialect>());
        builder.Services.AddSingleton(static sp => new DbSession(
            sp.GetRequiredService<SqlProviderOptions>(),
            sp.GetRequiredService<ISqlDialect>(),
            sp.GetRequiredService<SqlResourceCatalog>()
        ));
        builder.Services.AddSingleton<IDbSession>(static sp => sp.GetRequiredService<DbSession>());
        builder.Services.AddSingleton<IProviderBootstrap, PostgresProviderBootstrap>();

        // Provider-owned feature stores: each implements a core store port over this package's own
        // embedded SQL, bound and mapped directly in the store.
        builder.Services.AddSingleton(static sp => new SqlResourceCatalog(
            typeof(PostgresDialect).Assembly,
            sp.GetRequiredService<SqlProviderOptions>().Schema
        ));
        builder.Services.AddSingleton<IOverviewStore, RelationalOverviewStore>();
        builder.Services.AddSingleton<IEventStore, RelationalEventStore>();
        builder.Services.AddSingleton<IDefinitionStore, RelationalDefinitionStore>();
        builder.Services.AddSingleton<INamespaceStore, RelationalNamespaceStore>();
        builder.Services.AddSingleton<ITenantStore, RelationalTenantStore>();
        builder.Services.AddSingleton<IJobStore, RelationalJobStore>();
        builder.Services.AddSingleton<ITagStore, RelationalTagStore>();
        builder.Services.AddSingleton<ISignalStore, RelationalSignalStore>();
        builder.Services.AddSingleton<IScheduleStore, RelationalScheduleStore>();
        builder.Services.AddSingleton<IWorkerStore, RelationalWorkerStore>();
        builder.Services.AddSingleton<IAlertStore, RelationalAlertStore>();
        builder.Services.AddSingleton<IRetentionStore, RelationalRetentionStore>();
        builder.Services.AddSingleton<IExecutionStore, RelationalExecutionStore>();
        builder.Services.TryAddSingleton<ILockStore, RelationalLockStore>();
        builder.Services.AddSingleton<IServerClock, RelationalActaClock>();
        builder.Services.TryAddSingleton<IActaClock>(sp => sp.GetRequiredService<IServerClock>());

        return builder;
    }
}
