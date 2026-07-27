using Acta.Sqlite.Schema;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Acta.Tests.Schema;

public sealed class SchemaMigrationCompatibilityTests
{
    [Fact]
    public async Task Earlier_preview_M001_is_rejected_with_reprovisioning_guidance()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE migrations (
                    version integer NOT NULL PRIMARY KEY,
                    name text NOT NULL,
                    applied_at_utc text NOT NULL,
                    installed_schema text NOT NULL
                ) STRICT;
                INSERT INTO migrations (version, name, applied_at_utc, installed_schema)
                VALUES (1, 'init', '2026-01-01T00:00:00Z', 'main');
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqliteSchemaMigrator.ApplyAsync(connection, "main", cancellationToken)
        );

        // Names the stale baseline the database is on and says what to do about it. The stamp this
        // build ships is deliberately not asserted: it is bumped on every `schema reset`, and this
        // test should survive that rather than have to be edited alongside it.
        Assert.Contains("'init'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("drop and reprovision", exception.Message, StringComparison.Ordinal);
    }
}
