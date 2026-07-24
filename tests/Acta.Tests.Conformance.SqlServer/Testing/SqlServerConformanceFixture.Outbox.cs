using System.Globalization;
using Acta;
using Acta.Tests.Conformance.Testing;
using Microsoft.Data.SqlClient;

namespace Acta.Tests.Conformance.SqlServer.Testing;

/// <summary>
/// SQL Server external-outbox fixture surface: table creation routed through the tested provider DDL API
/// (single-sourced canonical outbox shape) plus seed/read helpers. Per-test table names keep parallel
/// specs isolated in the shared <c>acta_test</c> schema; the DDL derives constraint/index names from the
/// table so they never collide.
/// </summary>
public sealed partial class SqlServerConformanceFixture
{
    private static string Schema => IntegrationConfig.TestSchemaName;

    private static string Conn =>
        new SqlConnectionStringBuilder(IntegrationConfig.SqlServerConnectionString!) { TrustServerCertificate = true }.ConnectionString;

    // Single-source every fixture table from the tested provider DDL API; drop the target table first so a
    // prior run's table is replaced. Constraint/index names derive from the table, so no cross-table collision.
    public async ValueTask ApplyOutboxDdlAsync(string table)
    {
        await ExecOutboxAsync($"DROP TABLE IF EXISTS {Schema}.{table};");
        await ExecOutboxAsync(Acta.SqlServerOutboxDdl.CreateScript(table, Schema));
    }

    public async ValueTask<(int BusinessRows, int OutboxRows)> StageWithBusinessWriteAsync(
        string outboxTable,
        Acta.JobEnqueueRequest request,
        bool commit
    )
    {
        var probe = "acta_txn_stage_" + Guid.NewGuid().ToString("N")[..12];
        await ExecOutboxAsync($"CREATE TABLE {Schema}.{probe} (marker int NOT NULL);");
        await using (var c = new SqlConnection(Conn))
        {
            await c.OpenAsync();
            await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
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
        await using var c = new SqlConnection(Conn);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {qualified};";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    public object CreateOutboxStore(string table) => Acta.SqlServerOutboxSource.CreateStore(Conn, Schema, table);

    public void ApplyOutboxSource(Acta.IOutboxSourceBuilder source)
    {
        source.Schema = Schema;
        source.UseSqlServer(o => o.ConnectionString = Conn);
    }

    private static async ValueTask ExecOutboxAsync(string sql)
    {
        await using var c = new SqlConnection(Conn);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask SeedOutboxRowAsync(string table, OutboxSeed seed)
    {
        await using var c = new SqlConnection(Conn);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {Schema}.{table}
                (outbox_id, job_namespace, job_name, input_format_id, input_data, deduplication_key,
                 meta, priority_code, created_at_utc, next_attempt_at_utc, status_code, failure_count,
                 claim_token, claim_until_utc)
            VALUES (@id, @ns, @name, @fmt, @data, @dedup, @meta, @prio, @created, @next, @status, @failures,
                    @token, @until);
            """;
        cmd.Parameters.Add(new SqlParameter("@id", System.Data.SqlDbType.UniqueIdentifier) { Value = seed.OutboxId });
        cmd.Parameters.Add(new SqlParameter("@ns", System.Data.SqlDbType.VarChar, 64) { Value = seed.JobNamespace });
        cmd.Parameters.Add(new SqlParameter("@name", System.Data.SqlDbType.VarChar, 128) { Value = seed.JobName });
        cmd.Parameters.Add(new SqlParameter("@fmt", System.Data.SqlDbType.TinyInt) { Value = seed.InputFormatId });
        cmd.Parameters.Add(new SqlParameter("@data", System.Data.SqlDbType.VarBinary) { Value = (object?)seed.InputData ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@dedup", System.Data.SqlDbType.VarChar, 128) { Value = seed.DeduplicationKey });
        cmd.Parameters.Add(new SqlParameter("@meta", System.Data.SqlDbType.NVarChar, -1) { Value = (object?)seed.Meta ?? DBNull.Value });
        cmd.Parameters.Add(
            new SqlParameter("@prio", System.Data.SqlDbType.TinyInt) { Value = seed.PriorityCode is { } p ? p : DBNull.Value }
        );
        cmd.Parameters.Add(new SqlParameter("@created", System.Data.SqlDbType.DateTime2) { Value = seed.CreatedAtUtc });
        cmd.Parameters.Add(new SqlParameter("@next", System.Data.SqlDbType.DateTime2) { Value = seed.NextAttemptAtUtc });
        cmd.Parameters.Add(new SqlParameter("@status", System.Data.SqlDbType.TinyInt) { Value = seed.StatusCode });
        cmd.Parameters.Add(new SqlParameter("@failures", System.Data.SqlDbType.Int) { Value = seed.FailureCount });
        cmd.Parameters.Add(
            new SqlParameter("@token", System.Data.SqlDbType.UniqueIdentifier) { Value = seed.ClaimToken is { } t ? t : DBNull.Value }
        );
        cmd.Parameters.Add(
            new SqlParameter("@until", System.Data.SqlDbType.DateTime2) { Value = seed.ClaimUntilUtc is { } u ? u : DBNull.Value }
        );
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask<OutboxRowState> ReadOutboxRowAsync(string table, Guid outboxId)
    {
        await using var c = new SqlConnection(Conn);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT status_code, failure_count, claim_token, claim_until_utc, next_attempt_at_utc, last_error
              FROM {Schema}.{table} WHERE outbox_id = @id;
            """;
        cmd.Parameters.Add(new SqlParameter("@id", System.Data.SqlDbType.UniqueIdentifier) { Value = outboxId });
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync())
        {
            return default;
        }

        return new OutboxRowState(
            Exists: true,
            StatusCode: r.GetByte(0),
            FailureCount: r.GetInt32(1),
            ClaimToken: r.IsDBNull(2) ? null : r.GetGuid(2),
            ClaimUntilUtc: r.IsDBNull(3) ? null : Utc(r.GetDateTime(3)),
            NextAttemptAtUtc: Utc(r.GetDateTime(4)),
            LastError: r.IsDBNull(5) ? null : r.GetString(5)
        );
    }

    public async ValueTask<int> CountOutboxAsync(string table)
    {
        await using var c = new SqlConnection(Conn);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {Schema}.{table};";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    public async ValueTask RewindOutboxAsync(string table)
    {
        await using var c = new SqlConnection(Conn);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"UPDATE {Schema}.{table} SET next_attempt_at_utc = DATEADD(hour, -1, SYSUTCDATETIME()) WHERE status_code = 10;";
        await cmd.ExecuteNonQueryAsync();
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
