using Acta.Tests.Conformance.Sqlite.Testing;
using Acta.Tests.Conformance.Testing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Acta.Tests.Conformance.Sqlite.Features.Outbox;

/// <summary>
/// A producer may stage an id without its dashes or in upper case, and SQLite keeps it as spelled, so every relay
/// statement matches an id in its dashless lower-case form: the release, the acknowledgement, the reschedule, the
/// quarantine, and the operator's list, match, requeue, and discard. Provider-specific, since PostgreSQL and SQL Server
/// store a native UUID.
/// </summary>
public sealed class SqliteOutboxIdSpellingTests : ActaTestBase<SqliteConformanceFixture>
{
    private const int LeaseTtlSeconds = 180;

    [Fact(DisplayName = "Every relay statement matches an id staged without dashes or in upper case")]
    public async Task Every_relay_statement_matches_any_spelling()
    {
        var ct = TestContext.Current.CancellationToken;
        var table = $"acta_outbox_ids_{Guid.NewGuid():N}"[..28];
        await Fixture.ApplyOutboxDdlAsync(table);
        var (delivered, retried, poisoned) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await StageAsync(table, delivered.ToString("N"), ct);
        await StageAsync(table, retried.ToString("N").ToUpperInvariant(), ct);
        await StageAsync(table, poisoned.ToString().ToUpperInvariant(), ct);
        var store = Fixture.OpenOutboxSource(table);

        // A released claim returns every spelling to Pending, so the claim below takes all three again.
        var released = Guid.NewGuid();
        Assert.Equal(3, (await store.ClaimAsync(new ClaimOutboxCommand(released, 10, LeaseTtlSeconds), ct)).Rows.Count);
        await store.ReleaseClaimedAsync(new ReleaseClaimedOutboxCommand(released, [delivered, retried, poisoned]), ct);
        Assert.Equal(["10:0", "10:0", "10:0"], await StatesAsync(table, ct));

        var token = Guid.NewGuid();
        var claimed = (await store.ClaimAsync(new ClaimOutboxCommand(token, 10, LeaseTtlSeconds), ct)).Rows;
        Assert.Equal([delivered, retried, poisoned], claimed.Select(r => r.OutboxId));
        await store.RemoveClaimedAsync(new RemoveClaimedOutboxCommand(token, [delivered]), ct);
        await store.RescheduleAsync(new RescheduleOutboxCommand(token, [new OutboxReschedule(retried, 1, 60, "recoverable")]), ct);
        await store.QuarantineAsync(new QuarantineOutboxCommand(token, [new OutboxQuarantine(poisoned, 5, "poison")]), ct);
        Assert.Equal(["10:1", "90:5"], await StatesAsync(table, ct));

        Assert.Equal(
            poisoned,
            Assert.Single((await store.ListQuarantinedRowsAsync(new ListQuarantinedOutboxRowsQuery(10, null), ct)).Items).OutboxId
        );
        Assert.Empty((await store.ListQuarantinedRowsAsync(new ListQuarantinedOutboxRowsQuery(10, poisoned), ct)).Items);
        Assert.Equal([poisoned], (await store.ListQuarantinedIdsAsync(new ListQuarantinedOutboxIdsQuery([poisoned]), ct)).OutboxIds);
        Assert.Equal([poisoned], (await store.RequeueQuarantinedAsync(new RequeueQuarantinedOutboxCommand([poisoned]), ct)).OutboxIds);
        Assert.Equal(["10:1", "10:0"], await StatesAsync(table, ct));

        var again = Guid.NewGuid();
        Assert.Equal(
            poisoned,
            Assert.Single((await store.ClaimAsync(new ClaimOutboxCommand(again, 10, LeaseTtlSeconds), ct)).Rows).OutboxId
        );
        await store.QuarantineAsync(new QuarantineOutboxCommand(again, [new OutboxQuarantine(poisoned, 5, "poison")]), ct);
        Assert.Equal([poisoned], (await store.DiscardQuarantinedAsync(new DiscardQuarantinedOutboxCommand([poisoned]), ct)).OutboxIds);
        Assert.Equal(["10:1"], await StatesAsync(table, ct));
    }

    private static async Task StageAsync(string table, string outboxId, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(SqliteIntegrationSchema.BootstrappedConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"INSERT INTO main.{table} (outbox_id, job_namespace, job_name, input_format_id, deduplication_key, "
            + "created_at_utc, next_attempt_at_utc) VALUES (@id, 'orders', 'send', 0, @dedup, "
            + "strftime('%Y-%m-%d %H:%M:%f', 'now', '-5 minutes'), strftime('%Y-%m-%d %H:%M:%f', 'now', '-5 minutes'))";
        cmd.Parameters.Add(new SqliteParameter("@id", SqliteType.Text) { Value = outboxId });
        cmd.Parameters.Add(new SqliteParameter("@dedup", SqliteType.Text) { Value = Guid.NewGuid().ToString("N") });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Each remaining row's status and failure count, in staging order.</summary>
    private static async Task<List<string>> StatesAsync(string table, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(SqliteIntegrationSchema.BootstrappedConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT status_code || ':' || failure_count FROM main.{table} ORDER BY id";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var states = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            states.Add(reader.GetString(0));
        }

        return states;
    }
}
