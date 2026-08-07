using Acta.Relational.Schema;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Acta.Tests.Schema;

/// <summary>
/// Multi-migration chain behavior of the shared migration runner, exercised end-to-end against
/// in-memory SQLite. The real providers ship only M001, so the chain paths (apply-in-order, resume
/// from a partial history, the name-drift guard, the version-0 stamp requirement) are untestable
/// through them; this test assembly embeds a fabricated M001+M002 pair under provider-shaped
/// logical resource names (see Fixtures/MigrationChain) so discovery resolves it as a provider.
/// </summary>
public sealed class SchemaMigrationChainTests
{
    private static readonly SchemaMigrationProviderHooks Hooks = new(
        ProviderAssembly: typeof(SchemaMigrationChainTests).Assembly,
        DialectToken: "sqlite",
        SplitBatches: static script => [script]
    );

    private static Task Apply(SqliteConnection conn) =>
        SchemaMigrationRunner.ApplyAsync(conn, "main", Hooks, TestContext.Current.CancellationToken);

    private static async Task<SqliteConnection> OpenFreshAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        return conn;
    }

    private static async Task<Dictionary<int, string>> HistoryAsync(SqliteConnection conn)
    {
        var rows = new Dictionary<int, string>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version, name FROM main.migrations ORDER BY version;";
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(reader.GetInt32(0), reader.GetString(1));
        }

        return rows;
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection conn, string table)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        cmd.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken)) == 1;
    }

    private static async Task ExecAsync(SqliteConnection conn, string sqlText)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sqlText;
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Fixture_baseline_stamp_matches_the_shipped_stamp()
    {
        using var stream = typeof(SchemaMigrationChainTests).Assembly.GetManifestResourceStream(
            "Acta.Tests.Schema.Migrations.M001_init.sql"
        )!;
        using var reader = new StreamReader(stream);
        var fixture = reader.ReadToEnd();

        // A `schema reset` bumps the stamp in SqlDdlDialect.BaselineStamp and
        // SchemaMigrationRunner.RequiredBaselineStamp; this fixture is the third copy. Update the
        // literal in Fixtures/MigrationChain/M001_init.sql when this fails.
        Assert.Contains(
            $"VALUES (0, '{SchemaMigrationRunner.RequiredBaselineStamp}', '{{{{schema}}}}')",
            fixture,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task Fresh_chain_applies_both_migrations_and_records_plain_names()
    {
        await using var conn = await OpenFreshAsync();
        await Apply(conn);

        var history = await HistoryAsync(conn);
        Assert.Equal(3, history.Count);
        Assert.Equal(SchemaMigrationRunner.RequiredBaselineStamp, history[0]);
        Assert.Equal("init", history[1]);
        Assert.Equal("add_widgets", history[2]);
        Assert.True(await TableExistsAsync(conn, "gadgets"));
        Assert.True(await TableExistsAsync(conn, "widgets"));
    }

    [Fact]
    public async Task Rerun_applies_nothing()
    {
        await using var conn = await OpenFreshAsync();
        await Apply(conn);
        await Apply(conn);

        Assert.Equal(3, (await HistoryAsync(conn)).Count);
    }

    [Fact]
    public async Task Resume_applies_only_the_missing_tail()
    {
        await using var conn = await OpenFreshAsync();
        await Apply(conn);
        // Rewind to the M001-only shape a database upgraded from an older build would present.
        await ExecAsync(conn, "DROP TABLE main.widgets; DELETE FROM main.migrations WHERE version = 2;");

        await Apply(conn);

        Assert.True(await TableExistsAsync(conn, "widgets"));
        Assert.Equal("add_widgets", (await HistoryAsync(conn))[2]);
    }

    [Fact]
    public async Task Renamed_applied_migration_is_rejected()
    {
        await using var conn = await OpenFreshAsync();
        await Apply(conn);
        await ExecAsync(conn, "UPDATE main.migrations SET name = 'add_flags' WHERE version = 2;");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Apply(conn));
        Assert.Contains("'add_flags'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'add_widgets'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task History_without_the_stamp_row_demands_reprovisioning()
    {
        await using var conn = await OpenFreshAsync();
        await Apply(conn);
        await ExecAsync(conn, "DELETE FROM main.migrations WHERE version = 0;");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Apply(conn));
        Assert.Contains("drop and reprovision", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'init'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task History_ahead_of_the_shipped_chain_is_accepted()
    {
        await using var conn = await OpenFreshAsync();
        await Apply(conn);
        // A database already carrying a migration this build does not ship (older worker, newer
        // database) boots normally: unknown versions are simply not this build's to verify.
        await ExecAsync(conn, "INSERT INTO main.migrations (version, name, installed_schema) VALUES (3, 'add_sprockets', 'main');");

        await Apply(conn);

        Assert.Equal(4, (await HistoryAsync(conn)).Count);
    }
}
