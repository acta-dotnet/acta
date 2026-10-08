using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Acta.Sqlite;

/// <summary>
/// SQLite provider registration: the dialect, bootstrap, and options; core owns everything else. SQLite has no stored
/// routines, so every command runs as inline SQL and bulk shapes are bound as JSON.
/// </summary>
public static class SqliteActaBuilderExtensions
{
    /// <summary>
    /// Selects SQLite as Acta's durable provider. SQLite is single-node and embedded; the schema is always <c>main</c>
    /// (the attached database holds the tables).
    /// </summary>
    public static IActaBuilder UseSqlite(this IActaBuilder builder, Action<SqliteProviderOptions> configure)
    {
        var acta = ActaProviderRegistration.Use<SqliteProviderOptions, SqliteDialect, SqliteProviderBootstrap>(
            builder,
            DbProvider.Sqlite,
            configure,
            static sp => new SqliteDialect(sp.GetRequiredService<IOptions<JobsOptions>>().Value.ExecutionProfile)
        );
        acta.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<SqliteProviderOptions>, SqliteProviderOptionsValidator>()
        );
        return acta;
    }
}
