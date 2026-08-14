using Acta.Runtime.Modules.Execution.Api;
using Acta.Runtime.Modules.Outbox;
using Acta.Tests.Conformance.Postgres.Testing;
using Acta.Tests.Conformance.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Conformance.Postgres.Features.Outbox;

/// <summary>
/// The single mixed-provider case: a SQLite external-outbox source relayed into a PostgreSQL ledger,
/// proving the source and target provider registrations are fully independent - a worker relays a source
/// database on a different provider than its own ledger. Drives the real relay composition
/// (<see cref="OutboxRelayService"/> over the SQLite source store and the owned Postgres target). Concrete
/// (not a generic candidate spec), so it binds no per-provider head and adds no matrix.
/// </summary>
public sealed class PgSqliteMixedRelaySpec : ActaRuntimeTestBase<PgConformanceFixture, TestJobs.TestJobsManifest>
{
    private string _sqlitePath = null!;
    private string _sqliteConn = null!;

    protected override async ValueTask AfterInitializeAsync()
    {
        await base.AfterInitializeAsync();
        _sqlitePath = Path.Combine(Path.GetTempPath(), $"acta-mixed-{Guid.NewGuid():N}.db");
        _sqliteConn = new SqliteConnectionStringBuilder { DataSource = _sqlitePath }.ConnectionString;
        await CreateCanonicalSqliteOutboxAsync();
    }

    protected override ValueTask BeforeDisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_sqlitePath);
        }
        catch (IOException) { }
        return ValueTask.CompletedTask;
    }

    [Fact(DisplayName = "A SQLite outbox source relays into a Postgres ledger, delivering once and deleting the source row")]
    public async Task Sqlite_source_relays_into_a_postgres_ledger()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var dedup = TestKey("mixed");
        await SeedSqliteRowAsync(Guid.NewGuid(), dedup);

        // Source store on SQLite, target on the Postgres ledger: two independent provider registrations.
        var store = Acta.Sqlite.Hosting.SqliteOutboxSource.CreateStore(_sqliteConn, "main", "acta_outbox");
        var target = new JobsSubmission(Jobs);
        var relay = new OutboxRelayService(store, target);
        var maxInline = Services.GetRequiredService<IOptions<JobsOptions>>().Value.MaxInlinePayloadBytes;

        await relay.RunTickAsync(new OutboxRelayTickOptions("sqlite-src", 5, 180, maxInline), ct);

        // The SQLite source row is gone (delivered and finalized) and the Postgres ledger has exactly one job.
        Assert.Equal(0, await CountSqliteRowsAsync());
        Assert.Equal(1, await PgConformanceFixture.CountLedgerJobsByDedupAsync(ns, dedup));
    }

    // Single-source the canonical table from the tested SQLite DDL API rather than duplicating the text.
    private async Task CreateCanonicalSqliteOutboxAsync()
    {
        await using var c = new SqliteConnection(_sqliteConn);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = Acta.Sqlite.Hosting.SqliteOutboxDdl.CreateScript("acta_outbox");
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SeedSqliteRowAsync(Guid outboxId, string dedup)
    {
        var payload = Services
            .GetRequiredService<IJobPayloadSerializerRegistry>()
            .Resolve(JobPayloadFormat.Json.Id)
            .Serialize(new TestJobs.Echo("mixed"));
        await using var c = new SqliteConnection(_sqliteConn);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO main.acta_outbox
                (outbox_id, job_namespace, job_name, input_format_id, input, deduplication_key,
                 created_at_utc, next_attempt_at_utc, status_code, failure_count)
            VALUES (@id, @ns, 'echo', 1, @data, @dedup,
                    strftime('%Y-%m-%d %H:%M:%f','now','-5 minutes'),
                    strftime('%Y-%m-%d %H:%M:%f','now','-5 minutes'), 10, 0);
            """;
        // Raw Guid so Microsoft.Data.Sqlite stores the true UPPER-CASE TEXT id an EF producer would write.
        cmd.Parameters.AddWithValue("@id", outboxId);
        cmd.Parameters.AddWithValue("@ns", TestNamespace);
        cmd.Parameters.AddWithValue("@data", payload.Data.ToArray());
        cmd.Parameters.AddWithValue("@dedup", dedup);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<int> CountSqliteRowsAsync()
    {
        await using var c = new SqliteConnection(_sqliteConn);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM main.acta_outbox;";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
