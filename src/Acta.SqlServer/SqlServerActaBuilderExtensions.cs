using Acta.Runtime;

namespace Acta.SqlServer;

/// <summary>SQL Server provider registration: the dialect, bootstrap, and options; core owns everything else.</summary>
public static class SqlServerActaBuilderExtensions
{
    /// <summary>Selects SQL Server as Acta's durable provider, configured by its connection string and schema options.</summary>
    public static IActaBuilder UseSqlServer(this IActaBuilder builder, Action<SqlServerProviderOptions> configure) =>
        ActaProviderRegistration.Use<SqlServerProviderOptions, SqlServerDialect, SqlServerProviderBootstrap>(
            builder,
            DbProvider.SqlServer,
            configure
        );
}
