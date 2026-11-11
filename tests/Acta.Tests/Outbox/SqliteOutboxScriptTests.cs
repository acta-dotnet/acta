using System.Globalization;
using Acta.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Acta.Tests.Outbox;

/// <summary>
/// The SQLite outbox table takes an id with or without its dashes, in either case, holds one row per UUID however it is
/// spelled, as the relay matches ids, and takes only values the claim projection can read into its byte, int, and
/// DateTime fields.
/// </summary>
public sealed class SqliteOutboxScriptTests
{
    private const string Id = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    [Theory]
    [InlineData("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE")]
    [InlineData("aaaaaaaabbbbccccddddeeeeeeeeeeee")]
    [InlineData("AAAAAAAABBBBCCCCDDDDEEEEEEEEEEEE")]
    public async Task One_id_has_one_row_however_it_is_spelled(string spelling)
    {
        await using var db = await OpenAsync();
        await InsertAsync(db, Id);

        var refused = await Assert.ThrowsAsync<SqliteException>(() => InsertAsync(db, spelling));
        Assert.Contains("UNIQUE constraint failed", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_id_without_dashes_is_kept()
    {
        await using var db = await OpenAsync();

        await InsertAsync(db, "aaaaaaaabbbbccccddddeeeeeeeeeeee");
        await InsertAsync(db, Guid.NewGuid().ToString("N").ToUpperInvariant());
    }

    [Theory]
    [InlineData("{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}")]
    [InlineData("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeeg")]
    [InlineData("aaaaaaaab-bbb-cccc-dddd-eeeeeeeeeeee")]
    [InlineData("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeee-")]
    [InlineData("aaaaaaaa-bbbb-cccc-dddd-eeeeeee")]
    [InlineData("aaaaaaaabbbbccccddddeeeeeeeeeee")]
    [InlineData("")]
    [InlineData("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\0garbage")]
    [InlineData("aaaaaaaabbbbccccddddeeeeeeeeeeee\0abc")]
    public async Task An_id_that_is_not_a_uuid_is_refused(string outboxId)
    {
        await using var db = await OpenAsync();

        var refused = await Assert.ThrowsAsync<SqliteException>(() => InsertAsync(db, outboxId));
        Assert.Contains("ck_acta_outbox_outbox_id_uuid", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("job_priority_code", 256L, "ck_acta_outbox_priority_range")]
    [InlineData("job_priority_code", -1L, "ck_acta_outbox_priority_range")]
    [InlineData("delay_seconds", 2147483648L, "ck_acta_outbox_delay_nonneg")]
    [InlineData("failure_count", 2147483648L, "ck_acta_outbox_failure_nonneg")]
    public async Task A_value_the_relay_cannot_project_is_refused(string column, long value, string check)
    {
        await using var db = await OpenAsync();

        var refused = await Assert.ThrowsAsync<SqliteException>(() => InsertAsync(db, Id, (column, value)));
        Assert.Contains(check, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_largest_values_the_relay_projects_are_kept()
    {
        await using var db = await OpenAsync();

        await InsertAsync(db, Id, ("job_priority_code", 255L));
        await InsertAsync(db, Guid.NewGuid().ToString(), ("delay_seconds", (long)int.MaxValue));
        await InsertAsync(db, Guid.NewGuid().ToString().ToUpperInvariant(), ("failure_count", (long)int.MaxValue));
        await using var count = db.CreateCommand();
        count.CommandText = "SELECT count(*) FROM acta_outbox";
        Assert.Equal(
            3L,
            Convert.ToInt64(await count.ExecuteScalarAsync(TestContext.Current.CancellationToken), CultureInfo.InvariantCulture)
        );
    }

    [Theory]
    [InlineData("not-a-date")]
    [InlineData("2026-02-30 12:00:00.000")]
    [InlineData("2026-10-31 24:00:00.000")]
    [InlineData("2026-10-15 24:00:00.000")]
    [InlineData("0000-01-01 00:00:00.000")]
    [InlineData("2461345.5")]
    [InlineData("2026-10-31T12:00:00+02:00")]
    [InlineData("2026-10-31 12:00")]
    [InlineData("2026-10-31 12:00:00 tomorrow")]
    public async Task A_run_instant_that_is_not_utc_text_sqlite_reads_as_written_is_refused(string instant)
    {
        await using var db = await OpenAsync();

        var refused = await Assert.ThrowsAsync<SqliteException>(() => InsertAsync(db, Id, ("next_run_at_utc", instant)));
        Assert.Contains("ck_acta_outbox_next_run_instant", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0001-01-01 00:00:00.000")]
    [InlineData("2024-02-29 23:59:59.999")]
    [InlineData("9999-12-31 23:59:59.999")]
    [InlineData("2026-10-31 12:00:00")]
    [InlineData("2026-10-31T12:00:00Z")]
    [InlineData("2026-10-31T12:00:00.1234567Z")]
    public async Task A_utc_run_instant_is_kept(string instant)
    {
        await using var db = await OpenAsync();

        await InsertAsync(db, Id, ("next_run_at_utc", instant));
    }

    [Theory]
    [InlineData("UTF-16le")]
    [InlineData("UTF-16be")]
    public async Task A_utf16_database_keeps_both_spellings_and_refuses_a_nul(string encoding)
    {
        await using var db = await OpenAsync(encoding);

        await InsertAsync(db, Id);
        await InsertAsync(db, Guid.NewGuid().ToString("N"));
        var refused = await Assert.ThrowsAsync<SqliteException>(() => InsertAsync(db, Guid.NewGuid() + "\0garbage"));
        Assert.Contains("ck_acta_outbox_outbox_id_uuid", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A fresh in-memory database holding the table, in <paramref name="encoding"/> when one is named.</summary>
    private static async Task<SqliteConnection> OpenAsync(string? encoding = null)
    {
        var db = new SqliteConnection("Data Source=:memory:");
        await db.OpenAsync();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = (encoding is null ? "" : $"PRAGMA encoding = '{encoding}';") + SqliteMigrations.CreateOutboxScript();
        await cmd.ExecuteNonQueryAsync();
        return db;
    }

    /// <summary>A minimal Pending row, with one more column set when <paramref name="extra"/> names one.</summary>
    private static async Task InsertAsync(SqliteConnection db, string outboxId, (string Column, object Value)? extra = null)
    {
        await using var cmd = db.CreateCommand();
        var column = extra is { } e ? ", " + e.Column : "";
        var value = extra is null ? "" : ", @value";
        cmd.CommandText =
            $"INSERT INTO acta_outbox (outbox_id, namespace_name, job_name, input_format_id, deduplication_key{column}) "
            + $"VALUES (@id, 'orders', 'send', 0, @dedup{value})";
        cmd.Parameters.AddWithValue("@id", outboxId);
        cmd.Parameters.AddWithValue("@dedup", Guid.NewGuid().ToString("N"));
        if (extra is { } set)
        {
            cmd.Parameters.AddWithValue("@value", set.Value);
        }

        await cmd.ExecuteNonQueryAsync();
    }
}
