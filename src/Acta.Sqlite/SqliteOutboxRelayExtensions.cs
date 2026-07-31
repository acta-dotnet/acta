using Acta.Runtime.Hosting;
using Acta.Sqlite.Configuration;
using Acta.Sqlite.Hosting;

namespace Acta.Sqlite;

/// <summary>
/// Selects SQLite as an external-outbox relay source. Captures its own connection options and records
/// the provider selection without touching the Acta-ledger provider registration; it does not connect
/// at startup. The executable claim/finalize SQL and source connection creation are owned by this
/// provider package.
/// </summary>
public static class SqliteOutboxRelayExtensions
{
    public static IOutboxSourceBuilder UseSqlite(this IOutboxSourceBuilder source, Action<SqliteOutboxSourceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new SqliteOutboxSourceOptions();
        configure(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ConnectionString, nameof(options.ConnectionString));

        ((OutboxSourceBuilder)source).SetStoreFactory(new SqliteOutboxSourceStoreFactory(options.ConnectionString));
        return source;
    }
}
