using System.Data.Common;
using System.Globalization;
using Acta.Relational.Outbox;
using Acta.Sqlite.Services;
using Microsoft.Data.Sqlite;

namespace Acta.Sqlite;

/// <summary>
/// Transactional external-outbox staging for SQLite producers. Validates a <see cref="JobEnqueueRequest"/>
/// through the shared staging core and executes one canonical <c>acta_outbox</c> INSERT on the caller's own
/// open <see cref="SqliteTransaction"/>, so the outbox row commits or rolls back with the producer's own
/// business writes. Zero configuration: no dependency injection, no <c>AddActa</c>, no ledger connection;
/// reference the package, write <c>using Acta;</c>, and call the extension. The database defaults populate
/// the operational columns; the worker-side relay drains the table.
/// </summary>
public static class SqliteOutboxStagingExtensions
{
    public static Task AddToActaOutboxAsync(
        this SqliteTransaction transaction,
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
        // Explicit SqliteType (never AddWithValue, per the AOT parameter-metadata policy). The raw Guid binds
        // to TEXT with Microsoft.Data.Sqlite's canonical encoding; instants use the millisecond ISO text.
        p.Add(new SqliteParameter("@outbox_id", SqliteType.Text) { Value = row.OutboxId });
        p.Add(new SqliteParameter("@job_namespace", SqliteType.Text) { Value = row.JobNamespace });
        p.Add(new SqliteParameter("@job_name", SqliteType.Text) { Value = row.JobName });
        p.Add(new SqliteParameter("@input_format_id", SqliteType.Integer) { Value = (long)row.InputFormatId });
        p.Add(new SqliteParameter("@input", SqliteType.Blob) { Value = (object?)row.Input ?? DBNull.Value });
        p.Add(new SqliteParameter("@deduplication_key", SqliteType.Text) { Value = row.DeduplicationKey });
        p.Add(new SqliteParameter("@correlation_key", SqliteType.Text) { Value = (object?)row.CorrelationKey ?? DBNull.Value });
        p.Add(new SqliteParameter("@exclusive_key", SqliteType.Text) { Value = (object?)row.ExclusiveKey ?? DBNull.Value });
        p.Add(
            new SqliteParameter("@priority_code", SqliteType.Integer) { Value = row.PriorityCode is { } code ? (long)code : DBNull.Value }
        );
        p.Add(
            new SqliteParameter("@next_run_at_utc", SqliteType.Text)
            {
                Value = row.NextRunAtUtc is { } next
                    ? next.ToString(SqliteOutboxDialect.InstantFormat, CultureInfo.InvariantCulture)
                    : DBNull.Value,
            }
        );
        p.Add(
            new SqliteParameter("@delay_seconds", SqliteType.Integer) { Value = row.DelaySeconds is { } delay ? (long)delay : DBNull.Value }
        );
        p.Add(new SqliteParameter("@tenant_key", SqliteType.Text) { Value = (object?)row.TenantKey ?? DBNull.Value });
        p.Add(new SqliteParameter("@meta", SqliteType.Text) { Value = (object?)row.Meta ?? DBNull.Value });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
