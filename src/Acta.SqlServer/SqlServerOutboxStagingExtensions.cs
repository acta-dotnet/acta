using System.Data;
using System.Data.Common;
using Acta.Relational.Outbox;
using Microsoft.Data.SqlClient;

namespace Acta;

/// <summary>
/// Transactional external-outbox staging for SQL Server producers. Validates a <see cref="JobEnqueueRequest"/>
/// through the shared staging core and executes one canonical <c>acta_outbox</c> INSERT on the caller's own
/// open <see cref="SqlTransaction"/>, so the outbox row commits or rolls back with the producer's own
/// business writes. Zero configuration: no dependency injection, no <c>AddActa</c>, no ledger connection;
/// reference the package, write <c>using Acta;</c>, and call the extension. The database defaults populate
/// the operational columns; the worker-side relay drains the table.
/// </summary>
public static class SqlServerOutboxStagingExtensions
{
    public static Task AddToActaOutboxAsync(
        this SqlTransaction transaction,
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
        p.Add(new SqlParameter("@outbox_id", SqlDbType.UniqueIdentifier) { Value = row.OutboxId });
        p.Add(new SqlParameter("@job_namespace", SqlDbType.VarChar, 64) { Value = row.JobNamespace });
        p.Add(new SqlParameter("@job_name", SqlDbType.VarChar, 128) { Value = row.JobName });
        p.Add(new SqlParameter("@input_format_id", SqlDbType.TinyInt) { Value = row.InputFormatId });
        p.Add(new SqlParameter("@input_data", SqlDbType.VarBinary, -1) { Value = (object?)row.InputData ?? DBNull.Value });
        p.Add(new SqlParameter("@deduplication_key", SqlDbType.VarChar, 128) { Value = row.DeduplicationKey });
        p.Add(new SqlParameter("@correlation_key", SqlDbType.VarChar, 64) { Value = (object?)row.CorrelationKey ?? DBNull.Value });
        p.Add(new SqlParameter("@exclusive_key", SqlDbType.VarChar, 128) { Value = (object?)row.ExclusiveKey ?? DBNull.Value });
        p.Add(new SqlParameter("@priority_code", SqlDbType.TinyInt) { Value = row.PriorityCode is { } code ? code : DBNull.Value });
        p.Add(new SqlParameter("@next_run_at_utc", SqlDbType.DateTime2) { Value = (object?)row.NextRunAtUtc ?? DBNull.Value });
        p.Add(new SqlParameter("@delay_seconds", SqlDbType.Int) { Value = (object?)row.DelaySeconds ?? DBNull.Value });
        p.Add(new SqlParameter("@tenant_key", SqlDbType.VarChar, 128) { Value = (object?)row.TenantKey ?? DBNull.Value });
        p.Add(new SqlParameter("@meta", SqlDbType.NVarChar, -1) { Value = (object?)row.Meta ?? DBNull.Value });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
