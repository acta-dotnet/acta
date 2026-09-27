using Acta.Relational.Commands;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Settings for the race specs, which exist to catch a deadlock on the servers. SQLite runs one writer at a
/// time, so no deadlock can form there, and its conformance tests share one database file.
/// </summary>
internal static class DeadlockRetryOff
{
    /// <summary>
    /// Turns deadlock retry off on the servers, where a retry would hide the deadlock the spec exists to
    /// catch. SQLite keeps its retry, which makes a writer waiting out another transaction a wait rather
    /// than a failure.
    /// </summary>
    public static void Apply(IServiceProvider services, SqlProviderOptions options)
    {
        if (services.GetRequiredService<ISqlDialect>().Provider != DbProvider.Sqlite)
        {
            options.DeadlockRetryAttempts = 1;
        }
    }

    /// <summary>
    /// Skips a heavy race on SQLite: it cannot deadlock there, and holding the one writer through it starves
    /// every test running beside it on the shared database file.
    /// </summary>
    public static void SkipOnSqlite(IServiceProvider services) =>
        Assert.SkipWhen(
            services.GetRequiredService<ISqlDialect>().Provider == DbProvider.Sqlite,
            "SQLite runs one writer at a time, so this race cannot deadlock there."
        );
}
