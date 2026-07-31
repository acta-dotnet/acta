using System.Globalization;
using Acta.Tests.Conformance.Testing;
using Microsoft.Data.Sqlite;

namespace Acta.Tests.Conformance.Sqlite.Testing;

/// <summary>
/// SQLite external-outbox fixture surface: table creation routed through the tested provider DDL API
/// (single-sourced canonical outbox shape) plus seed/read helpers. Per-test table names keep parallel
/// specs isolated in the single <c>main</c> database; the DDL derives constraint/index names from the
/// table so they never collide.
/// </summary>
public sealed partial class SqliteConformanceFixture
{
    private const string IsoFormat = "yyyy-MM-dd HH:mm:ss.fff";

    // Single-source every fixture table from the tested provider DDL API; drop the target table first so a
    // prior run's table is replaced. Constraint/index names derive from the table, so no cross-table collision.
    public async ValueTask ApplyOutboxDdlAsync(string table)
    {
        await ExecOutboxAsync($"DROP TABLE IF EXISTS main.{table};");
        await ExecOutboxAsync(Acta.Sqlite.Hosting.SqliteOutboxDdl.CreateScript(table));
    }

    public async ValueTask<(int BusinessRows, int OutboxRows)> StageWithBusinessWriteAsync(
        string outboxTable,
        Acta.JobEnqueueRequest request,
        bool commit
    )
    {
        var probe = "acta_txn_stage_" + Guid.NewGuid().ToString("N")[..12];
        await ExecOutboxAsync($"CREATE TABLE main.{probe} (marker INTEGER NOT NULL);");
        await using (var c = new SqliteConnection(SqliteIntegrationSchema.BootstrappedConnectionString))
        {
            await c.OpenAsync();
            await using var tx = (SqliteTransaction)await c.BeginTransactionAsync();
            await using (var biz = c.CreateCommand())
            {
                biz.Transaction = tx;
                biz.CommandText = $"INSERT INTO main.{probe} (marker) VALUES (1);";
                await biz.ExecuteNonQueryAsync();
            }
            await tx.AddToActaOutboxAsync(request, outboxTable, "main");
            if (commit)
            {
                await tx.CommitAsync();
            }
            else
            {
                await tx.RollbackAsync();
            }
        }

        return (await CountRowsAsync($"main.{probe}"), await CountOutboxAsync(outboxTable));
    }

    private static async ValueTask<int> CountRowsAsync(string qualified)
    {
        await using var c = new SqliteConnection(SqliteIntegrationSchema.BootstrappedConnectionString);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {qualified};";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    public object CreateOutboxStore(string table) =>
        Acta.Sqlite.Hosting.SqliteOutboxSource.CreateStore(SqliteIntegrationSchema.BootstrappedConnectionString, "main", table);

    public void ApplyOutboxSource(Acta.IOutboxSourceBuilder source)
    {
        source.Schema = "main";
        source.UseSqlite(o => o.ConnectionString = SqliteIntegrationSchema.BootstrappedConnectionString);
    }

    private static async ValueTask ExecOutboxAsync(string sql)
    {
        await using var c = new SqliteConnection(SqliteIntegrationSchema.BootstrappedConnectionString);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask SeedOutboxRowAsync(string table, OutboxSeed seed)
    {
        await using var c = new SqliteConnection(SqliteIntegrationSchema.BootstrappedConnectionString);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO main.{table}
                (outbox_id, job_namespace, job_name, input_format_id, input_data, deduplication_key,
                 meta, priority_code, created_at_utc, next_attempt_at_utc, status_code, failure_count,
                 claim_token, claim_until_utc)
            VALUES (@id, @ns, @name, @fmt, @data, @dedup, @meta, @prio, @created, @next, @status, @failures,
                    @token, @until);
            """;
        // Bind the raw Guid (not .ToString()) so Microsoft.Data.Sqlite applies its real UPPER-CASE TEXT
        // encoding, reproducing the true EF producer row the relay finalize must match case-insensitively.
        cmd.Parameters.AddWithValue("@id", seed.OutboxId);
        cmd.Parameters.AddWithValue("@ns", seed.JobNamespace);
        cmd.Parameters.AddWithValue("@name", seed.JobName);
        cmd.Parameters.AddWithValue("@fmt", (long)seed.InputFormatId);
        cmd.Parameters.AddWithValue("@data", (object?)seed.InputData ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@dedup", seed.DeduplicationKey);
        cmd.Parameters.AddWithValue("@meta", (object?)seed.Meta ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@prio", seed.PriorityCode is { } p ? (long)p : DBNull.Value);
        cmd.Parameters.AddWithValue("@created", seed.CreatedAtUtc.ToString(IsoFormat, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@next", seed.NextAttemptAtUtc.ToString(IsoFormat, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@status", (long)seed.StatusCode);
        cmd.Parameters.AddWithValue("@failures", (long)seed.FailureCount);
        cmd.Parameters.AddWithValue("@token", seed.ClaimToken is { } t ? t : (object)DBNull.Value);
        cmd.Parameters.AddWithValue(
            "@until",
            seed.ClaimUntilUtc is { } u ? u.ToString(IsoFormat, CultureInfo.InvariantCulture) : DBNull.Value
        );
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask<OutboxRowState> ReadOutboxRowAsync(string table, Guid outboxId)
    {
        await using var c = new SqliteConnection(SqliteIntegrationSchema.BootstrappedConnectionString);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT status_code, failure_count, claim_token, claim_until_utc, next_attempt_at_utc, last_error
              FROM main.{table} WHERE outbox_id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", outboxId);
        await using var r = await cmd.ExecuteReaderAsync();
        return !await r.ReadAsync()
            ? default
            : new OutboxRowState(
                Exists: true,
                StatusCode: (byte)r.GetInt64(0),
                FailureCount: (int)r.GetInt64(1),
                ClaimToken: r.IsDBNull(2) ? null : Guid.Parse(r.GetString(2)),
                ClaimUntilUtc: r.IsDBNull(3) ? null : ParseIso(r.GetString(3)),
                NextAttemptAtUtc: ParseIso(r.GetString(4)),
                LastError: r.IsDBNull(5) ? null : r.GetString(5)
            );
    }

    public async ValueTask<int> CountOutboxAsync(string table)
    {
        await using var c = new SqliteConnection(SqliteIntegrationSchema.BootstrappedConnectionString);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM main.{table};";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    public async ValueTask RewindOutboxAsync(string table)
    {
        await using var c = new SqliteConnection(SqliteIntegrationSchema.BootstrappedConnectionString);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            $"UPDATE main.{table} SET next_attempt_at_utc = strftime('%Y-%m-%d %H:%M:%f', 'now', '-1 hour') WHERE status_code = 10;";
        await cmd.ExecuteNonQueryAsync();
    }

    private static DateTime ParseIso(string value) =>
        DateTime.SpecifyKind(DateTime.Parse(value, CultureInfo.InvariantCulture), DateTimeKind.Utc);
}
