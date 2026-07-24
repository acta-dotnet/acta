using Acta.Postgres.Configuration;

namespace Acta
{
    /// <summary>
    /// Selects PostgreSQL as an external-outbox relay source. Captures its own connection options and
    /// records the provider selection; it never touches the process's Acta-ledger provider registration
    /// and does not connect at startup. The executable claim/finalize SQL and source connection creation
    /// are owned by this provider package.
    /// </summary>
    public static class PostgresOutboxRelayExtensions
    {
        public static IOutboxSourceBuilder UsePostgres(this IOutboxSourceBuilder source, Action<PostgresOutboxSourceOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(configure);

            var options = new PostgresOutboxSourceOptions();
            configure(options);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.ConnectionString, nameof(options.ConnectionString));

            ((OutboxSourceBuilder)source).SetStoreFactory(new PostgresOutboxSourceStoreFactory(options.ConnectionString));
            return source;
        }
    }
}

namespace Acta.Postgres.Configuration
{
    /// <summary>
    /// Connection options for a PostgreSQL external-outbox source, captured on
    /// <c>source.UsePostgres(...)</c>. Independent of the ledger's <c>PostgresProviderOptions</c>: the
    /// relay may target a different database or provider than the worker's own ledger. Schema and table
    /// overrides live on the source builder; nothing here connects at startup.
    /// </summary>
    public sealed class PostgresOutboxSourceOptions
    {
        /// <summary>Connection string for the producer's PostgreSQL outbox database. Required.</summary>
        public string ConnectionString { get; set; } = "";
    }
}
