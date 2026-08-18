using Acta.Relational;
using Acta.Relational.Commands;
using Acta.Relational.Connections;
using Acta.Relational.Resources;
using Acta.Runtime.Hosting;
using Acta.Sqlite.Configuration;
using Acta.Sqlite.Hosting;
using Acta.Sqlite.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acta.Sqlite;

/// <summary>
/// SQLite provider registration. Core owns feature behavior; this package supplies the complete
/// provider store set, command binding, executable SQL, and relational mechanics. SQLite has no
/// stored routines, so store commands run as inline SQL and bulk shapes are bound as JSON.
/// </summary>
public static class SqliteActaBuilderExtensions
{
    /// <summary>
    /// Selects SQLite as Acta's durable provider. Registers connection string/schema options,
    /// relational mechanics, provider bootstrap, and provider-owned feature stores. SQLite is single-node and
    /// embedded; the schema is always <c>main</c> (the attached database holds the tables).
    /// </summary>
    public static IActaBuilder UseSqlite(this IActaBuilder builder, Action<SqliteProviderOptions> configure)
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
        // GetService, not GetRequiredService: a bare host may register no logging at all, and the
        // session falls back to the null logger rather than failing to resolve.
        builder.Services.AddSingleton(static sp => new DbSession(
            sp.GetRequiredService<SqlProviderOptions>(),
            sp.GetRequiredService<ISqlDialect>(),
            sp.GetRequiredService<SqlResourceCatalog>(),
            sp.GetService<ILogger<DbSession>>()
        ));
        builder.Services.AddSingleton<IDbSession>(static sp => sp.GetRequiredService<DbSession>());
        builder.Services.AddSingleton<IProviderBootstrap, SqliteProviderBootstrap>();

        // Provider-owned feature stores: each implements a core store port over this package's own
        // embedded SQL, bound and mapped directly in the store.
        builder.Services.AddSingleton(static sp => new SqlResourceCatalog(
            typeof(SqliteDialect).Assembly,
            sp.GetRequiredService<SqlProviderOptions>().Schema
        ));
        builder.Services.AddActaRelationalStores();

        return builder;
    }
}
