using Acta.Emit.Features.Docs;
using Acta.Relational.Schema;
using Acta.Sqlite.Schema;
using Acta.Tests.Conformance.Testing;
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

    [Theory]
    [InlineData("UPDATE main.migrations SET name = 'objects-1.999-future' WHERE version = -1;")]
    [InlineData("INSERT INTO main.migrations (version, name, installed_schema) VALUES (999, 'future', 'main');")]
    public async Task Older_bootstrap_and_script_leave_a_newer_release_in_place(string newerRelease)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        await SqliteSchemaMigrator.ApplyAsync(connection, "main", ct);

        // Stands in for a later release: a higher package revision or a migration this build lacks,
        // plus a view it appended a column to.
        await Execute(
            connection,
            newerRelease
                + """

                DROP VIEW main.jobs_view;
                CREATE VIEW main.jobs_view AS SELECT 1 AS future_marker;
                """,
            ct
        );
        var history = await History(connection, ct);

        await SqliteSchemaMigrator.ApplyAsync(connection, "main", ct);

        var script = ProvisionScriptEmitter.Emit(IntegrationConfig.FindRepoRoot(), "sqlite");
        var refused = await Assert.ThrowsAsync<SqliteException>(() => Execute(connection, script, ct));
        Assert.Contains("newer_acta_release_installed", refused.Message, StringComparison.Ordinal);
        await Execute(connection, "ROLLBACK;", ct);

        Assert.Equal(history, await History(connection, ct));
        await using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT future_marker FROM main.jobs_view";
        Assert.Equal(1L, await probe.ExecuteScalarAsync(ct));
    }

    [Fact]
    public void Applied_migration_renamed_on_disk_is_rejected_instead_of_skipped()
    {
        var shipped = new[] { new SchemaMigration(1, "M001_init", ""), new SchemaMigration(2, "M002_add_flags", "") };

        // Matching bare names pass; the version-0 stamp row is not a migration and is never compared.
        SchemaMigrationRunner.VerifyAppliedNames(
            shipped,
            new Dictionary<int, string>
            {
                [0] = "some-stamp",
                [1] = "init",
                [2] = "add_flags",
            }
        );
        // A version the database has not applied yet is free to differ.
        SchemaMigrationRunner.VerifyAppliedNames(shipped, new Dictionary<int, string> { [1] = "init" });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SchemaMigrationRunner.VerifyAppliedNames(shipped, new Dictionary<int, string> { [1] = "init", [2] = "add_columns" })
        );
        Assert.Contains("'add_columns'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'add_flags'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("drop and reprovision", exception.Message, StringComparison.Ordinal);
    }

    private static async Task Execute(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        // Stepped by hand: the script opens with a PRAGMA that returns a row, and a reader disposed
        // there runs the remaining statements without raising their errors.
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.NextResultAsync(ct)) { }
    }

    private static async Task<string> History(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT group_concat(version || '=' || name, ';') FROM (SELECT version, name FROM main.migrations ORDER BY version)";
        return (string)(await command.ExecuteScalarAsync(ct))!;
    }
}
