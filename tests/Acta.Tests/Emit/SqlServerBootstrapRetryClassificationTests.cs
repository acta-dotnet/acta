using Acta.SqlServer;
using Xunit;

namespace Acta.Tests.Emit;

/// <summary>
/// What the SQL Server install retries: an open retries a refused login, once before the login is proven; the step starts
/// again only on the errors another installer's guard causes, so a timeout or a deadlock fails at once.
/// </summary>
public sealed class SqlServerBootstrapRetryClassificationTests
{
    [Theory]
    [InlineData(4060, true)] // a database another installer is creating or switching
    [InlineData(18456, true)] // the same switch refuses a login with this number too
    [InlineData(-2, false)] // a connect timeout is not waited out a second time
    [InlineData(10061, false)] // nothing listens there
    public void An_open_retries_only_a_refused_login(int number, bool retries) =>
        Assert.Equal(retries, SqlServerSchemaMigrator.RetriesOpen(number));

    [Theory]
    [InlineData(new[] { 596 })] // session killed by another installer's ROLLBACK IMMEDIATE
    [InlineData(new[] { 5069, 596, 0 })] // a killed ALTER reports "ALTER DATABASE statement failed" ahead of the kill
    [InlineData(new[] { 5061 })] // another installer's ALTER holds the database
    [InlineData(new[] { 10054 })] // connection reset
    public void A_concurrent_installer_restarts_the_step(int[] errorNumbers) =>
        Assert.True(SqlServerSchemaMigrator.IsConcurrentInstall(errorNumbers));

    [Theory]
    [InlineData(new[] { -2 })] // a migration batch timed out behind a blocking workload: repeating it would block again
    [InlineData(new[] { 1205 })] // a deadlock is fixed at its cause, never retried
    [InlineData(new[] { 5069 })] // an ALTER that failed for its own reason
    [InlineData(new[] { 50000 })] // the guard refused a system database
    [InlineData(new[] { 229 })] // permission denied on object
    public void Other_errors_fail_the_step(int[] errorNumbers) => Assert.False(SqlServerSchemaMigrator.IsConcurrentInstall(errorNumbers));
}
