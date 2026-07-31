using Acta.Postgres.Configuration;
using Acta.Postgres.Hosting;
using Acta.Postgres.Services;
using Acta.Relational;
using Acta.Relational.Commands;
using Acta.Relational.Connections;
using Acta.Relational.Resources;
using Acta.Runtime.Hosting;
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
        builder.Services.AddActaRelationalStores();

        return builder;
    }
}
