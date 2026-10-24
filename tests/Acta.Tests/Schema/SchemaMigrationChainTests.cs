using Microsoft.Data.Sqlite;
using Xunit;

namespace Acta.Tests.Schema;

/// <summary>
/// Multi-migration chain behavior of the shared migration runner, exercised end-to-end against
/// in-memory SQLite. The real providers ship only a baseline, so the chain paths (apply in order,
/// resume from a partial history) are untestable through them; this test assembly embeds a
/// fabricated two-migration chain under provider-shaped logical resource names (see
/// Fixtures/MigrationChain) so discovery resolves it as a provider.
/// </summary>
public sealed class SchemaMigrationChainTests
{
    private const string Init = "M20260101000000_init";
    private const string AddWidgets = "M20260102000000_add_widgets";

    private static Task Apply(SqliteConnection conn) =>
        SchemaMigrations.ApplyAsync(
            conn,
            "main",
            "sqlite",
            typeof(SchemaMigrationChainTests).Assembly,
            TestContext.Current.CancellationToken
        );

    private static async Task<SqliteConnection> OpenFreshAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        return conn;
    }

    private static async Task<List<string>> HistoryAsync(SqliteConnection conn)
    {
        var rows = new List<string>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM main.migrations ORDER BY name;";
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(reader.GetString(0));
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
    public async Task Fresh_chain_applies_both_migrations_and_records_their_stems()
    {
        await using var conn = await OpenFreshAsync();
        await Apply(conn);

        Assert.Equal([Init, AddWidgets], await HistoryAsync(conn));
        Assert.True(await TableExistsAsync(conn, "gadgets"));
        Assert.True(await TableExistsAsync(conn, "widgets"));
    }

    [Fact]
    public async Task Rerun_applies_nothing()
    {
        await using var conn = await OpenFreshAsync();
        await Apply(conn);
        await Apply(conn);

        Assert.Equal(2, (await HistoryAsync(conn)).Count);
    }

    [Fact]
    public async Task Resume_applies_only_the_missing_tail()
    {
        await using var conn = await OpenFreshAsync();
        await Apply(conn);
        // Rewind to the baseline-only shape a database upgraded from an older build would present.
        await ExecAsync(conn, $"DROP TABLE main.widgets; DELETE FROM main.migrations WHERE name = '{AddWidgets}';");

        await Apply(conn);

        Assert.True(await TableExistsAsync(conn, "widgets"));
        Assert.Equal([Init, AddWidgets], await HistoryAsync(conn));
    }

    [Fact]
    public async Task History_ahead_of_the_shipped_chain_is_accepted()
    {
        await using var conn = await OpenFreshAsync();
        await Apply(conn);
        // A database already carrying a migration this build does not ship (older worker, newer
        // database) boots normally: unknown rows are simply not this build's to verify.
        await ExecAsync(conn, "INSERT INTO main.migrations (name) VALUES ('M20260103000000_add_sprockets');");

        await Apply(conn);

        Assert.Equal(3, (await HistoryAsync(conn)).Count);
    }

    [Fact]
    public async Task A_database_cut_from_another_baseline_is_refused_and_left_unchanged()
    {
        await using var conn = await OpenFreshAsync();
        await Apply(conn);
        // An earlier release candidate's cut: its baseline row under another name, and no widgets table yet.
        await ExecAsync(
            conn,
            $"DROP TABLE main.widgets; DELETE FROM main.migrations WHERE name = '{AddWidgets}'; "
                + $"UPDATE main.migrations SET name = 'M20251201000000_init' WHERE name = '{Init}';"
        );

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Apply(conn));

        Assert.Contains("M20251201000000_init", exception.Message, StringComparison.Ordinal);
        Assert.Equal(["M20251201000000_init"], await HistoryAsync(conn));
        Assert.False(await TableExistsAsync(conn, "widgets"));
    }
}
