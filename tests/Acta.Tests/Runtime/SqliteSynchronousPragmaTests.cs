using Acta.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// The per-connection commit-durability PRAGMA on SQLite: synchronous = FULL under every execution
/// profile. Read back from an opened connection rather than from the dialect's private text, so the
/// assertion is about what the database was actually told.
/// </summary>
public sealed class SqliteSynchronousPragmaTests
{
    // 2 = FULL, per the SQLite pragma value table.
    private const long Full = 2;

    [Fact(DisplayName = "Every connection syncs each commit")]
    public async Task Every_connection_syncs_each_commit()
    {
        var dialect = new SqliteDialect();

        await using var connection = dialect.CreateConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA synchronous;";
        var actual = Assert.IsType<long>(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));

        Assert.Equal(Full, actual);
        Assert.IsType<SqliteConnection>(connection);
    }
}
