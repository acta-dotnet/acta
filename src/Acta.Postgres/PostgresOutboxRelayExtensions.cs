using Acta.Postgres.Configuration;
using Acta.Postgres.Hosting;
using Acta.Runtime.Hosting;

namespace Acta.Postgres;

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
