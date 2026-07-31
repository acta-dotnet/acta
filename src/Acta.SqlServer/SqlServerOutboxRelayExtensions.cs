using Acta.Runtime.Hosting;
using Acta.SqlServer.Configuration;
using Acta.SqlServer.Hosting;

namespace Acta.SqlServer;

/// <summary>
/// Selects SQL Server as an external-outbox relay source. Captures its own connection options and
/// records the provider selection without touching the Acta-ledger provider registration; it does not
/// connect at startup. The executable claim/finalize SQL and source connection creation are owned by
/// this provider package.
/// </summary>
public static class SqlServerOutboxRelayExtensions
{
    public static IOutboxSourceBuilder UseSqlServer(this IOutboxSourceBuilder source, Action<SqlServerOutboxSourceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new SqlServerOutboxSourceOptions();
        configure(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ConnectionString, nameof(options.ConnectionString));

        ((OutboxSourceBuilder)source).SetStoreFactory(new SqlServerOutboxSourceStoreFactory(options.ConnectionString));
        return source;
    }
}
