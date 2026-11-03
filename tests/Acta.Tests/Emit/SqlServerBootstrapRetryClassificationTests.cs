using Acta.SqlServer;
using Xunit;

namespace Acta.Tests.Emit;

/// <summary>
/// What the SQL Server install retries: an open retries a database that is mid-switch, and a refused login only once the
/// login is proven; the step starts again only on the errors another installer's guard causes, so a wrong password, a
/// timeout, or a deadlock fails at once.
/// </summary>
public sealed class SqlServerBootstrapRetryClassificationTests
{
    [Theory]
    [InlineData(4060, false, true)] // the database is mid-switch: wait it out
    [InlineData(4060, true, true)]
    [InlineData(18456, false, false)] // most likely a wrong password: fail at once, and count no failed logins toward a lockout
    [InlineData(18456, true, true)] // the login worked before: the database is settling
    [InlineData(-2, true, false)] // a connect timeout is not waited out a second time
    public void An_open_retries_only_a_database_mid_switch(int number, bool loginProven, bool retries) =>
        Assert.Equal(retries, SqlServerSchemaMigrator.RetriesOpen(number, loginProven));

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
