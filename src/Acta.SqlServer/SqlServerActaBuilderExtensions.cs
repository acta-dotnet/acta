using Acta.Configuration;
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
using Acta.Features.Tags;
using Acta.Features.Tenants;
using Acta.Features.Workers;
using Acta.Relational.Commands;
using Acta.Relational.Connections;
using Acta.Relational.Resources;
using Acta.Relational.Stores;
using Acta.Services.Locks;
using Acta.Services.Time;
using Acta.SqlServer.Configuration;
using Acta.SqlServer.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Acta;

/// <summary>
/// SQL Server provider registration. Core owns feature behavior; this package supplies the complete
/// provider store set, command binding, executable SQL, and relational mechanics.
/// </summary>
public static class SqlServerActaBuilderExtensions
{
    /// <summary>
    /// Selects SQL Server as Acta's durable provider. Registers connection string/schema
    /// options, relational mechanics, provider bootstrap, and provider-owned feature stores.
    /// </summary>
    public static IActaBuilder UseSqlServer(this IActaBuilder builder, Action<SqlServerProviderOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        ActaProviderRegistration.Add(builder.Services, new ActaProviderInfo(DbProvider.SqlServer, SupportsRoutines: true));
        builder.Services.Configure(configure);
        builder.Services.AddOptions<SqlServerProviderOptions>().ValidateOnStart();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<SqlServerProviderOptions>, SqlProviderOptionsValidator<SqlServerProviderOptions>>()
        );

        // The dialect owns generic connection, parameter, routine, and transaction traits. Feature
        // stores below own their commands, executable SQL, binding, and projections directly.
        builder.Services.AddSingleton<SqlProviderOptions>(static sp => sp.GetRequiredService<IOptions<SqlServerProviderOptions>>().Value);
        builder.Services.AddSingleton<SqlServerDialect>();
        builder.Services.AddSingleton<ISqlDialect>(static sp => sp.GetRequiredService<SqlServerDialect>());
        builder.Services.AddSingleton(static sp => new DbSession(
            sp.GetRequiredService<SqlProviderOptions>(),
            sp.GetRequiredService<ISqlDialect>(),
            sp.GetRequiredService<SqlResourceCatalog>()
        ));
        builder.Services.AddSingleton<IDbSession>(static sp => sp.GetRequiredService<DbSession>());
        builder.Services.AddSingleton<IProviderBootstrap, SqlServerProviderBootstrap>();

        // Provider-owned feature stores: each implements a core store port over this package's own
        // embedded SQL, bound and mapped directly in the store.
        builder.Services.AddSingleton(static sp => new SqlResourceCatalog(
            typeof(SqlServerDialect).Assembly,
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
