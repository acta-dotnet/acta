using Acta.Tests.Conformance.Sqlite.Testing;
using Acta.Tests.Conformance.Testing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Acta.Tests.Conformance.Sqlite.Features.Outbox;

/// <summary>
/// A producer writing SQL of its own may spell a UTC run instant with a T or a trailing Z, and the claim reads every
/// spelling as the same instant. Provider-specific, since PostgreSQL and SQL Server store a native timestamp. A Z read
/// wrong only on a host whose local zone is not UTC, so that is where this fact tells the two apart.
/// </summary>
public sealed class SqliteOutboxRunInstantTests : ActaTestBase<SqliteConformanceFixture>
{
    [Fact(DisplayName = "The claim reads a run instant spelled with a T or a Z as the same UTC instant")]
    public async Task Every_utc_spelling_claims_as_the_same_instant()
    {
        var ct = TestContext.Current.CancellationToken;
        var table = $"acta_outbox_run_{Guid.NewGuid():N}"[..28];
        await Fixture.ApplyOutboxDdlAsync(table);
        string[] spellings = ["2026-11-01 12:00:00.500", "2026-11-01T12:00:00.5Z", "2026-11-01T12:00:00.5000000Z"];
        foreach (var spelling in spellings)
        {
            await StageAsync(table, spelling, ct);
        }

        var claimed = (await Fixture.OpenOutboxSource(table).ClaimAsync(new ClaimOutboxCommand(Guid.NewGuid(), 10, 180), ct)).Rows;

        var expected = new DateTime(2026, 11, 1, 12, 0, 0, 500, DateTimeKind.Utc);
        Assert.Equal(spellings.Length, claimed.Count);
        Assert.All(claimed, row => Assert.Equal(expected, row.NextRunAtUtc));
    }

    private static async Task StageAsync(string table, string nextRunAtUtc, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(SqliteIntegrationSchema.BootstrappedConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"INSERT INTO main.{table} (outbox_id, namespace_name, job_name, input_format_id, deduplication_key, next_run_at_utc, "
            + "next_attempt_at_utc) VALUES (@id, 'orders', 'send', 0, @dedup, @next, strftime('%Y-%m-%d %H:%M:%f', 'now', '-5 minutes'))";
        cmd.Parameters.Add(new SqliteParameter("@id", SqliteType.Text) { Value = Guid.NewGuid().ToString() });
        cmd.Parameters.Add(new SqliteParameter("@dedup", SqliteType.Text) { Value = Guid.NewGuid().ToString("N") });
        cmd.Parameters.Add(new SqliteParameter("@next", SqliteType.Text) { Value = nextRunAtUtc });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
