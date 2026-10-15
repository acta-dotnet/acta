using Acta.Emit.Features.Docs;
using Acta.Sqlite;
using Acta.Tests.Conformance.Testing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Acta.Tests.Schema;

public sealed class SchemaMigrationCompatibilityTests
{
    [Fact]
    public async Task An_older_script_leaves_a_newer_release_in_place()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        await SqliteSchemaMigrator.ApplyAsync(connection, "main", ct);

        // Stands in for a later release: a migration this build lacks, which added a table.
        await Execute(
            connection,
            """
            INSERT INTO main.migrations (version, name, installed_schema) VALUES (999, 'future', 'main');
            CREATE TABLE main.future_marker (marker INTEGER NOT NULL) STRICT;
            INSERT INTO main.future_marker (marker) VALUES (1);
            """,
            ct
        );
        var history = await History(connection, ct);

        var script = ProvisionScriptEmitter.Emit(IntegrationConfig.FindRepoRoot(), "sqlite");
        var refused = await Assert.ThrowsAsync<SqliteException>(() => Execute(connection, script, ct));
        Assert.Contains("newer_acta_release_installed", refused.Message, StringComparison.Ordinal);
        await Execute(connection, "ROLLBACK;", ct);

        Assert.Equal(history, await History(connection, ct));
        await using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT marker FROM main.future_marker";
        Assert.Equal(1L, await probe.ExecuteScalarAsync(ct));
    }

    [Fact]
    public void Applied_migration_renamed_on_disk_is_rejected_instead_of_skipped()
    {
        var shipped = new[] { new SchemaMigration(1, "M001_init", ""), new SchemaMigration(2, "M002_add_flags", "") };

        SchemaMigrationRunner.VerifyAppliedNames(shipped, new Dictionary<int, string> { [1] = "init", [2] = "add_flags" });
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
