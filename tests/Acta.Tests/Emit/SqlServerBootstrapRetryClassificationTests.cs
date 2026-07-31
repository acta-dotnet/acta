using Acta.SqlServer.Schema;
using Xunit;

namespace Acta.Tests.Emit;

/// <summary>
/// The SQL Server bootstrap retry loops admit only the documented transient error numbers, so a
/// permanent configuration error fails on the first attempt instead of burning the bounded 30s
/// retry budget.
/// </summary>
public sealed class SqlServerBootstrapRetryClassificationTests
{
    [Theory]
    [InlineData(1205)] // deadlock victim
    [InlineData(1807)] // concurrent CREATE DATABASE holds the model lock
    [InlineData(5061)] // database in transition (concurrent ALTER)
    [InlineData(4060)] // cannot open database yet (mid-create/restart)
    [InlineData(18456)] // login rejected while the freshly-bounced database settles
    [InlineData(-2)] // client timeout
    [InlineData(233)] // connection killed mid-restart
    public void Documented_transient_numbers_retry(int number) => Assert.True(SqlServerSchemaMigrator.IsTransientBootstrapNumber(number));

    [Theory]
    [InlineData(102)] // syntax error
    [InlineData(229)] // permission denied on object
    [InlineData(262)] // CREATE DATABASE permission denied
    [InlineData(2760)] // schema name errors
    [InlineData(4064)] // default database unusable (account misconfiguration)
    public void Permanent_configuration_numbers_fail_first_attempt(int number) =>
        Assert.False(SqlServerSchemaMigrator.IsTransientBootstrapNumber(number));
}
