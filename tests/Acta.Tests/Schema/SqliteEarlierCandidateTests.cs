using System.Globalization;
using Acta.Sqlite;
using Acta.Tests.Conformance.Testing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Acta.Tests.Schema;

/// <summary>
/// A file a release candidate provisioned before the SQLite prefix holds Acta's tables under their bare names. The apply,
/// the read-only start and the published script each refuse it before they write, where a new build would otherwise
/// provision its prefixed tables beside the old ones and start on an empty queue. A file whose application owns a
/// <c>migrations</c> table of its own provisions as any file does.
/// </summary>
public sealed class SqliteEarlierCandidateTests : IDisposable
{
    // The earlier baseline by the names it left in the file, its history row included.
    private const string EarlierCandidate = """
        CREATE TABLE migrations (name TEXT NOT NULL, applied_at_utc INTEGER NOT NULL DEFAULT 0, CONSTRAINT pk_migrations PRIMARY KEY (name)) STRICT;
        INSERT INTO migrations (name) VALUES ('M20261120124658_init');
        CREATE TABLE namespaces (namespace_id INTEGER PRIMARY KEY AUTOINCREMENT, namespace_name TEXT NOT NULL) STRICT;
        CREATE TABLE jobs (job_id INTEGER PRIMARY KEY AUTOINCREMENT, namespace_id INTEGER NOT NULL) STRICT;
        CREATE TABLE checkpoints (job_id INTEGER NOT NULL, checkpoint_name TEXT NOT NULL) STRICT;
        CREATE INDEX ix_jobs_namespace ON jobs (namespace_id);
        CREATE VIEW jobs_view AS SELECT job_id FROM jobs;
        """;

    // An ORM's history table, TypeORM's columns, whose name an earlier candidate also used.
    private const string ApplicationMigrations = """
        CREATE TABLE migrations (id INTEGER PRIMARY KEY AUTOINCREMENT, timestamp INTEGER NOT NULL, name TEXT NOT NULL);
        INSERT INTO migrations (timestamp, name) VALUES (1700000000000, 'CreateOrders1700000000000');
        """;

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"acta-earlier-{Guid.NewGuid():N}.db");

    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = _path }.ConnectionString;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task The_apply_refuses_an_earlier_candidates_file_and_leaves_it_as_it_was()
    {
        var ct = TestContext.Current.CancellationToken;
        await ExecuteAsync(EarlierCandidate, ct);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqliteMigrations.ApplyAsync(ConnectionString, cancellationToken: ct)
        );

        Assert.Contains("release notes", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, await CountPrefixedAsync(ct));
        Assert.Equal("delete", await ScalarAsync("PRAGMA journal_mode;", ct));
    }

    [Fact]
    public async Task The_read_only_start_refuses_an_earlier_candidates_file()
    {
        var ct = TestContext.Current.CancellationToken;
        await ExecuteAsync(EarlierCandidate, ct);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqliteSchemaMigrator.PreflightAsync(ConnectionString, "acta", ct)
        );

        Assert.Contains("release notes", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_published_script_refuses_an_earlier_candidates_file_and_leaves_it_as_it_was()
    {
        var ct = TestContext.Current.CancellationToken;
        await ExecuteAsync(EarlierCandidate, ct);

        var refused = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(PublishedScript(), ct));

        Assert.Contains("earlier_acta_candidate_tables", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, await CountPrefixedAsync(ct));
        Assert.Equal("delete", await ScalarAsync("PRAGMA journal_mode;", ct));
    }

    [Fact]
    public async Task An_application_migrations_table_provisions_through_the_apply_and_the_read_only_start()
    {
        var ct = TestContext.Current.CancellationToken;
        await ExecuteAsync(ApplicationMigrations, ct);

        var unprovisioned = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqliteSchemaMigrator.PreflightAsync(ConnectionString, "acta", ct)
        );
        Assert.Contains("is not provisioned", unprovisioned.Message, StringComparison.Ordinal);
        Assert.NotEmpty(await SqliteMigrations.ApplyAsync(ConnectionString, cancellationToken: ct));
        await SqliteSchemaMigrator.PreflightAsync(ConnectionString, "acta", ct);

        Assert.Equal("CreateOrders1700000000000", await ScalarAsync("SELECT group_concat(name) FROM main.migrations;", ct));
    }

    [Fact]
    public async Task An_application_migrations_table_provisions_through_the_published_script()
    {
        var ct = TestContext.Current.CancellationToken;
        await ExecuteAsync(ApplicationMigrations, ct);

        await ExecuteAsync(PublishedScript(), ct);

        Assert.NotEqual(0L, await ScalarAsync("SELECT COUNT(*) FROM main.acta_migrations;", ct));
        Assert.Equal("CreateOrders1700000000000", await ScalarAsync("SELECT group_concat(name) FROM main.migrations;", ct));
    }

    [Fact]
    public async Task A_schema_that_begins_with_sqlite_is_refused_before_the_file_opens()
    {
        var ct = TestContext.Current.CancellationToken;

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => SqliteMigrations.ApplyAsync(ConnectionString, "sqlite_acta", ct));

        Assert.Equal("schema", refused.ParamName);
        Assert.False(File.Exists(_path));
    }

    private static string PublishedScript() =>
        File.ReadAllText(Path.Combine(IntegrationConfig.FindRepoRoot(), "docs", "reference", "schema-sqlite.sql"));

    private async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        // Stepped by hand: the script runs a PRAGMA that returns a row, and a reader disposed there runs the remaining
        // statements without raising their errors.
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.NextResultAsync(ct)) { }
    }

    private async Task<long> CountPrefixedAsync(CancellationToken ct) =>
        Convert.ToInt64(
            await ScalarAsync("SELECT COUNT(*) FROM main.sqlite_master WHERE substr(name, 1, 5) = 'acta_';", ct),
            CultureInfo.InvariantCulture
        );

    private async Task<object?> ScalarAsync(string sql, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(ct);
    }
}
