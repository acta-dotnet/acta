using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// The race specs, which exist to catch a deadlock on the servers, where every fixture already runs with deadlock retry
/// off. SQLite runs one writer at a time, so no deadlock can form there, and its conformance tests share one database
/// file.
/// </summary>
internal static class DeadlockRetryOff
{
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
