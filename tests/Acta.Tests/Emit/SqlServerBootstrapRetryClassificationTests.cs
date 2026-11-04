using Acta.SqlServer;
using Xunit;

namespace Acta.Tests.Emit;

/// <summary>
/// What the SQL Server install retries: an open retries a refused login, once before the login is proven; any other
/// error fails at once.
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
}
