using Acta.Tests.Conformance.Testing;
using Npgsql;
using NpgsqlTypes;

namespace Acta.Tests.Conformance.Postgres.Testing;

/// <summary>
/// PostgreSQL external-outbox fixture surface: table creation routed through the tested provider DDL API
/// (single-sourced canonical outbox shape) plus seed/read helpers. Per-test table names keep parallel
/// specs isolated in the shared <c>acta_test</c> schema; the DDL derives constraint/index names from the
/// table so they never collide.
/// </summary>
public sealed partial class PgConformanceFixture
{
    private static string Conn => IntegrationConfig.PostgresConnectionString!;
    private static string Schema => IntegrationConfig.TestSchemaName;

    // Single-source every fixture table from the tested provider DDL API; drop the target table first so a
    // prior run's table is replaced. Constraint/index names derive from the table, so no cross-table collision.
    public async ValueTask ApplyOutboxDdlAsync(string table)
    {
        await ExecOutboxAsync($"DROP TABLE IF EXISTS {Schema}.{table};");
        await ExecOutboxAsync(Acta.Postgres.Hosting.PostgresOutboxDdl.CreateScript(table, Schema));
    }

    public async ValueTask<(int BusinessRows, int OutboxRows)> StageWithBusinessWriteAsync(
        string outboxTable,
        Acta.JobEnqueueRequest request,
        bool commit
    )
    {
        var probe = "acta_txn_stage_" + Guid.NewGuid().ToString("N")[..12];
        await ExecOutboxAsync($"CREATE TABLE {Schema}.{probe} (marker integer NOT NULL);");
        await using (var c = new NpgsqlConnection(Conn))
        {
            await c.OpenAsync();
            await using var tx = await c.BeginTransactionAsync();
            await using (var biz = c.CreateCommand())
            {
                biz.Transaction = tx;
                biz.CommandText = $"INSERT INTO {Schema}.{probe} (marker) VALUES (1);";
                await biz.ExecuteNonQueryAsync();
            }
            await tx.AddToActaOutboxAsync(request, outboxTable, Schema);
            if (commit)
            {
                await tx.CommitAsync();
            }
            else
            {
                await tx.RollbackAsync();
            }
        }

        return (await CountRowsAsync($"{Schema}.{probe}"), await CountOutboxAsync(outboxTable));
    }

    private static async ValueTask<int> CountRowsAsync(string qualified)
    {
        await using var c = new NpgsqlConnection(Conn);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {qualified};";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public object CreateOutboxStore(string table) => Acta.Postgres.Hosting.PostgresOutboxSource.CreateStore(Conn, Schema, table);

    public void ApplyOutboxSource(Acta.IOutboxSourceBuilder source)
    {
        source.Schema = Schema;
        source.UsePostgres(o => o.ConnectionString = Conn);
    }

    /// <summary>Count Postgres-ledger jobs in a namespace carrying a deduplication key (mixed-provider proof).</summary>
    public static async ValueTask<int> CountLedgerJobsByDedupAsync(short namespaceId, string dedup)
    {
        await using var c = new NpgsqlConnection(Conn);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {Schema}.jobs WHERE namespace_id = @ns AND deduplication_key = @dedup;";
        cmd.Parameters.Add(new NpgsqlParameter("@ns", NpgsqlDbType.Smallint) { Value = namespaceId });
        cmd.Parameters.Add(new NpgsqlParameter("@dedup", NpgsqlDbType.Varchar) { Value = dedup });
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async ValueTask ExecOutboxAsync(string sql)
    {
        await using var c = new NpgsqlConnection(Conn);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask SeedOutboxRowAsync(string table, OutboxSeed seed)
    {
        await using var c = new NpgsqlConnection(Conn);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {Schema}.{table}
                (outbox_id, job_namespace, job_name, input_format_id, input, deduplication_key,
                 meta, priority_code, created_at_utc, next_attempt_at_utc, status_code, failure_count,
                 claim_token, claim_until_utc)
            VALUES (@id, @ns, @name, @fmt, @data, @dedup, @meta, @prio, @created, @next, @status, @failures,
                    @token, @until);
            """;
        cmd.Parameters.Add(new NpgsqlParameter("@id", NpgsqlDbType.Uuid) { Value = seed.OutboxId });
        cmd.Parameters.Add(new NpgsqlParameter("@ns", NpgsqlDbType.Varchar) { Value = seed.JobNamespace });
        cmd.Parameters.Add(new NpgsqlParameter("@name", NpgsqlDbType.Varchar) { Value = seed.JobName });
        cmd.Parameters.Add(new NpgsqlParameter("@fmt", NpgsqlDbType.Smallint) { Value = (short)seed.InputFormatId });
        cmd.Parameters.Add(new NpgsqlParameter("@data", NpgsqlDbType.Bytea) { Value = (object?)seed.Input ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("@dedup", NpgsqlDbType.Varchar) { Value = seed.DeduplicationKey });
        cmd.Parameters.Add(new NpgsqlParameter("@meta", NpgsqlDbType.Jsonb) { Value = (object?)seed.Meta ?? DBNull.Value });
        cmd.Parameters.Add(
            new NpgsqlParameter("@prio", NpgsqlDbType.Smallint) { Value = seed.PriorityCode is { } p ? (short)p : DBNull.Value }
        );
        cmd.Parameters.Add(new NpgsqlParameter("@created", NpgsqlDbType.TimestampTz) { Value = Utc(seed.CreatedAtUtc) });
        cmd.Parameters.Add(new NpgsqlParameter("@next", NpgsqlDbType.TimestampTz) { Value = Utc(seed.NextAttemptAtUtc) });
        cmd.Parameters.Add(new NpgsqlParameter("@status", NpgsqlDbType.Smallint) { Value = (short)seed.StatusCode });
        cmd.Parameters.Add(new NpgsqlParameter("@failures", NpgsqlDbType.Integer) { Value = seed.FailureCount });
        cmd.Parameters.Add(new NpgsqlParameter("@token", NpgsqlDbType.Uuid) { Value = seed.ClaimToken is { } t ? t : DBNull.Value });
        cmd.Parameters.Add(
            new NpgsqlParameter("@until", NpgsqlDbType.TimestampTz) { Value = seed.ClaimUntilUtc is { } u ? Utc(u) : DBNull.Value }
        );
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask<OutboxRowState> ReadOutboxRowAsync(string table, Guid outboxId)
    {
        await using var c = new NpgsqlConnection(Conn);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT status_code, failure_count, claim_token, claim_until_utc, next_attempt_at_utc, last_error
              FROM {Schema}.{table} WHERE outbox_id = @id;
            """;
        cmd.Parameters.Add(new NpgsqlParameter("@id", NpgsqlDbType.Uuid) { Value = outboxId });
        await using var r = await cmd.ExecuteReaderAsync();
        return !await r.ReadAsync()
            ? default
            : new OutboxRowState(
                Exists: true,
                StatusCode: (byte)r.GetInt16(0),
                FailureCount: r.GetInt32(1),
                ClaimToken: r.IsDBNull(2) ? null : r.GetGuid(2),
                ClaimUntilUtc: r.IsDBNull(3) ? null : Utc(r.GetDateTime(3)),
                NextAttemptAtUtc: Utc(r.GetDateTime(4)),
                LastError: r.IsDBNull(5) ? null : r.GetString(5)
            );
    }

    public async ValueTask<int> CountOutboxAsync(string table)
    {
        await using var c = new NpgsqlConnection(Conn);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {Schema}.{table};";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async ValueTask RewindOutboxAsync(string table)
    {
        await using var c = new NpgsqlConnection(Conn);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"UPDATE {Schema}.{table} SET next_attempt_at_utc = now() - INTERVAL '1 hour' WHERE status_code = 10;";
        await cmd.ExecuteNonQueryAsync();
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
