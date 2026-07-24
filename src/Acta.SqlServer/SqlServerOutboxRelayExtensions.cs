using Acta.SqlServer.Configuration;

namespace Acta
{
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
}

namespace Acta.SqlServer.Configuration
{
    /// <summary>
    /// Connection options for a SQL Server external-outbox source, captured on
    /// <c>source.UseSqlServer(...)</c>. Independent of the ledger's <c>SqlServerProviderOptions</c>; schema
    /// and table overrides live on the source builder, and nothing here connects at startup.
    /// </summary>
    public sealed class SqlServerOutboxSourceOptions
    {
        /// <summary>Connection string for the producer's SQL Server outbox database. Required.</summary>
        public string ConnectionString { get; set; } = "";
    }
}
