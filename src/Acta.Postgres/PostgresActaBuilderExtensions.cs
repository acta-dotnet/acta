using Acta.Runtime;

namespace Acta.Postgres;

/// <summary>PostgreSQL provider registration: the dialect, bootstrap, and options; core owns everything else.</summary>
public static class PostgresActaBuilderExtensions
{
    /// <summary>Selects PostgreSQL as Acta's durable provider, configured by its connection string and schema options.</summary>
    public static IActaBuilder UsePostgres(this IActaBuilder builder, Action<PostgresProviderOptions> configure) =>
        ActaProviderRegistration.Use<PostgresProviderOptions, PostgresDialect, PostgresProviderBootstrap>(
            builder,
            DbProvider.Postgres,
            configure
        );
}
