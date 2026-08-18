using System.Data.Common;
using Acta.Relational.Outbox;
using Npgsql;
using NpgsqlTypes;

namespace Acta.Postgres;

/// <summary>
/// Transactional external-outbox staging for PostgreSQL producers. Validates a <see cref="JobEnqueueRequest"/>
/// through the shared staging core and executes one canonical <c>acta_outbox</c> INSERT on the caller's own
/// open <see cref="NpgsqlTransaction"/>, so the outbox row commits or rolls back with the producer's own
/// business writes. Zero configuration: no dependency injection, no <c>AddActa</c>, no ledger connection;
/// reference the package, write <c>using Acta;</c>, and call the extension. The database defaults populate
/// the operational columns (<c>created_at_utc</c>, <c>status_code</c>, <c>failure_count</c>,
/// <c>next_attempt_at_utc</c>); the worker-side relay drains the table.
/// </summary>
public static class PostgresOutboxStagingExtensions
{
    public static Task AddToActaOutboxAsync(
        this NpgsqlTransaction transaction,
        JobEnqueueRequest request,
        string table = "acta_outbox",
        string? schema = null,
        CancellationToken cancellationToken = default
    )
    {
        // Shared prologue: identifier qualification, request validation/projection, and the structural
        // transaction guard, all before any I/O and identical across every provider.
        var (row, connection, sql) = OutboxStaging.Prepare(transaction, request, table, schema);
        return InsertAsync(transaction, connection, row, sql, cancellationToken);
    }

    private static async Task InsertAsync(
        DbTransaction transaction,
        DbConnection connection,
        OutboxStagingRow row,
        string sql,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        var p = command.Parameters;
        p.Add(new NpgsqlParameter("@outbox_id", NpgsqlDbType.Uuid) { Value = row.OutboxId });
        p.Add(new NpgsqlParameter("@job_namespace", NpgsqlDbType.Varchar) { Value = row.JobNamespace });
        p.Add(new NpgsqlParameter("@job_name", NpgsqlDbType.Varchar) { Value = row.JobName });
        p.Add(new NpgsqlParameter("@input_format_id", NpgsqlDbType.Smallint) { Value = (short)row.InputFormatId });
        p.Add(new NpgsqlParameter("@input", NpgsqlDbType.Bytea) { Value = (object?)row.Input ?? DBNull.Value });
        p.Add(new NpgsqlParameter("@deduplication_key", NpgsqlDbType.Varchar) { Value = row.DeduplicationKey });
        p.Add(new NpgsqlParameter("@correlation_key", NpgsqlDbType.Varchar) { Value = (object?)row.CorrelationKey ?? DBNull.Value });
        p.Add(new NpgsqlParameter("@exclusive_key", NpgsqlDbType.Varchar) { Value = (object?)row.ExclusiveKey ?? DBNull.Value });
        p.Add(
            new NpgsqlParameter("@priority_code", NpgsqlDbType.Smallint)
            {
                Value = row.PriorityCode is { } code ? (short)code : DBNull.Value,
            }
        );
        p.Add(new NpgsqlParameter("@next_run_at_utc", NpgsqlDbType.TimestampTz) { Value = (object?)row.NextRunAtUtc ?? DBNull.Value });
        p.Add(new NpgsqlParameter("@delay_seconds", NpgsqlDbType.Integer) { Value = (object?)row.DelaySeconds ?? DBNull.Value });
        p.Add(new NpgsqlParameter("@tenant_key", NpgsqlDbType.Varchar) { Value = (object?)row.TenantKey ?? DBNull.Value });
        p.Add(new NpgsqlParameter("@meta", NpgsqlDbType.Jsonb) { Value = (object?)row.Meta ?? DBNull.Value });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
