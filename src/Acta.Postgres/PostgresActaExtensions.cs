namespace Acta.Postgres;

/// <summary>Selects PostgreSQL as Acta's durable provider or as an external-outbox relay source.</summary>
public static class PostgresActaExtensions
{
    /// <summary>Selects PostgreSQL as Acta's durable provider, configured by its connection string and schema options.</summary>
    public static IActaBuilder UsePostgres(this IActaBuilder builder, Action<SqlProviderOptions> configure) =>
        ActaProviderRegistration.Use<PostgresDialect, PostgresProviderBootstrap>(builder, DbProvider.Postgres, configure);

    /// <summary>
    /// Selects the producer's PostgreSQL database at <paramref name="connectionString"/> as the external-outbox relay
    /// source. The source is independent of the ledger's provider and may be another database or provider; schema and
    /// table overrides live on the source builder. Nothing connects at startup.
    /// </summary>
    public static IOutboxSourceBuilder UsePostgres(this IOutboxSourceBuilder source, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ((OutboxSourceBuilder)source).SetStoreFactory(new PostgresOutboxSourceFactory(connectionString));
        return source;
    }
}
