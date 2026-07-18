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
using Acta.Sqlite.Configuration;
using Acta.Sqlite.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Acta;

/// <summary>
/// SQLite provider registration. Core owns feature behavior; this package supplies the complete
/// provider store set, command binding, executable SQL, and relational mechanics. SQLite has no
/// stored routines, so store commands run as inline SQL and bulk shapes are bound as JSON.
/// </summary>
public static class SqliteJobsBuilderExtensions
{
    /// <summary>
    /// Selects SQLite as Acta's durable provider. Registers connection string/schema options,
    /// relational mechanics, provider bootstrap, and provider-owned feature stores. SQLite is single-node and
    /// embedded; the schema is always <c>main</c> (the attached database holds the tables).
    /// </summary>
    public static IJobsBuilder UseSqlite(this IJobsBuilder builder, Action<SqliteProviderOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        ActaProviderRegistration.Add(builder.Services, new ActaProviderInfo(DbProvider.Sqlite, SupportsRoutines: false));
        builder.Services.Configure(configure);
        builder.Services.AddOptions<SqliteProviderOptions>().ValidateOnStart();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<SqliteProviderOptions>, SqlProviderOptionsValidator<SqliteProviderOptions>>()
        );
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<SqliteProviderOptions>, SqliteProviderOptionsValidator>()
        );

        builder.Services.AddSingleton<SqlProviderOptions>(static sp => sp.GetRequiredService<IOptions<SqliteProviderOptions>>().Value);
        builder.Services.AddSingleton(static sp => new SqliteDialect(
            sp.GetRequiredService<IOptions<JobsOptions>>().Value.ExecutionProfile
        ));
        builder.Services.AddSingleton<ISqlDialect>(static sp => sp.GetRequiredService<SqliteDialect>());
        builder.Services.AddSingleton<DbSession>();
        builder.Services.AddSingleton<IDbSession>(static sp => sp.GetRequiredService<DbSession>());
        builder.Services.AddSingleton<IProviderBootstrap, SqliteProviderBootstrap>();

        // Provider-owned feature stores: each implements a core store port over this package's own
        // embedded SQL, bound and mapped directly in the store.
        builder.Services.AddSingleton(static sp => new SqlResourceCatalog(
            typeof(SqliteDialect).Assembly,
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
