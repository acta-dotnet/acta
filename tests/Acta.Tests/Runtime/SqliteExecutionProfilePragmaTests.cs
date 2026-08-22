using Acta.Sqlite.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// The per-connection commit-durability PRAGMA each execution profile selects on SQLite. Buffered
/// keeps synchronous = FULL; Direct relaxes it to NORMAL, and Bulk - which SQLite has no batched
/// completion routine for, so it degrades to Direct - relaxes it the same way. Read back from an
/// opened connection rather than from the dialect's private text, so the assertion is about what the
/// database was actually told.
/// </summary>
public sealed class SqliteExecutionProfilePragmaTests
{
    // 0 = OFF, 1 = NORMAL, 2 = FULL, per the SQLite pragma value table.
    private const long Normal = 1;
    private const long Full = 2;

    [Theory]
    [InlineData(ExecutionProfile.Buffered, Full)]
    [InlineData(ExecutionProfile.Direct, Normal)]
    [InlineData(ExecutionProfile.Bulk, Normal)]
    public async Task Each_profile_selects_its_commit_durability(ExecutionProfile profile, long expected)
    {
        var dialect = new SqliteDialect(profile);

        await using var connection = dialect.CreateConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA synchronous;";
        var actual = Assert.IsType<long>(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));

        Assert.Equal(expected, actual);
        Assert.IsType<SqliteConnection>(connection);
    }
}
