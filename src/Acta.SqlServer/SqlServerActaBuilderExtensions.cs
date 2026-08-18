using Acta.Relational;
using Acta.Relational.Commands;
using Acta.Relational.Connections;
using Acta.Relational.Resources;
using Acta.Runtime.Hosting;
using Acta.SqlServer.Configuration;
using Acta.SqlServer.Hosting;
using Acta.SqlServer.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acta.SqlServer;

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
        // GetService, not GetRequiredService: a bare host may register no logging at all, and the
        // session falls back to the null logger rather than failing to resolve.
        builder.Services.AddSingleton(static sp => new DbSession(
            sp.GetRequiredService<SqlProviderOptions>(),
            sp.GetRequiredService<ISqlDialect>(),
            sp.GetRequiredService<SqlResourceCatalog>(),
            sp.GetService<ILogger<DbSession>>()
        ));
        builder.Services.AddSingleton<IDbSession>(static sp => sp.GetRequiredService<DbSession>());
        builder.Services.AddSingleton<IProviderBootstrap, SqlServerProviderBootstrap>();

        // Provider-owned feature stores: each implements a core store port over this package's own
        // embedded SQL, bound and mapped directly in the store.
        builder.Services.AddSingleton(static sp => new SqlResourceCatalog(
            typeof(SqlServerDialect).Assembly,
            sp.GetRequiredService<SqlProviderOptions>().Schema
        ));
        builder.Services.AddActaRelationalStores();

        return builder;
    }
}
