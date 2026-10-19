using System.Data.Common;
using System.Text.Json;

namespace Acta;

/// <summary>
/// The EF-free staging core the provider packages consume: normalizes and validates a raw
/// <see cref="EnqueueJobRequest"/> through the shared enqueue validation from the <c>Acta</c> SDK (identifiers, tags,
/// schedule mutual exclusion, priority, payload pair) and layers the two stricter outbox-only rules on top, a
/// required deduplication key and no parent id, projecting a valid request into an <see cref="OutboxStagingValues"/>
/// with a client-generated <c>outbox_id</c>. <see cref="Prepare"/> runs the whole provider-neutral prologue
/// (identifier qualification, projection, transaction guard, INSERT text), leaving each provider its binding and execution.
/// </summary>
internal static class OutboxStaging
{
    /// <summary>
    /// The canonical INSERT column list and VALUES clause, byte-identical across every provider. The provider
    /// extension binds these named parameters with its own typed parameter objects.
    /// </summary>
    private const string InsertColumnsAndValues = """
            (outbox_id, job_namespace, job_name, input_format_id, input, deduplication_key,
             correlation_key, concurrency_key, lane, priority_code, next_run_at_utc, delay_seconds, tenant_name, meta)
        VALUES (@outbox_id, @job_namespace, @job_name, @input_format_id, @input, @deduplication_key,
                @correlation_key, @concurrency_key, @lane, @priority_code, @next_run_at_utc, @delay_seconds, @tenant_name, @meta);
        """;

    /// <summary>
    /// The provider-neutral staging prologue: validate/qualify the table reference, validate and project the
    /// request into a row, and structurally guard the caller transaction. Returns the row, the transaction's
    /// open connection, and the full canonical INSERT text. Runs before any I/O, so a bad request or a
    /// detached/closed transaction throws exactly like the transactional enqueue overloads.
    /// </summary>
    public static (OutboxStagingValues Row, DbConnection Connection, string Sql) Prepare(
        DbTransaction transaction,
        EnqueueJobRequest request,
        string table,
        string? schema
    )
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var tableRef = OutboxIdentifier.Qualify(table, schema);
        var row = Stage(request);
        var connection = CallerTransaction.RequireOpenConnection(transaction);
        return (row, connection, $"INSERT INTO {tableRef}\n{InsertColumnsAndValues}");
    }

    public static OutboxStagingValues Stage(EnqueueJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalized = JobEnqueueRequestValidation.NormalizeAndValidate(request, nameof(request));

        if (normalized.DeduplicationKey is null)
        {
            throw new ArgumentException(
                "An outbox record requires a non-null DeduplicationKey.",
                $"{nameof(request)}.{nameof(EnqueueJobRequest.DeduplicationKey)}"
            );
        }

        return normalized.ParentJobId is not null
            ? throw new ArgumentException(
                "An outbox record cannot carry a ParentJobId; external records request root jobs only.",
                $"{nameof(request)}.{nameof(EnqueueJobRequest.ParentJobId)}"
            )
            : new OutboxStagingValues(
                OutboxId: Guid.NewGuid(),
                JobNamespace: normalized.JobNamespace,
                JobName: normalized.JobName,
                InputFormatId: normalized.Input.Format.Id,
                Input: normalized.Input.IsNone ? null : normalized.Input.Data.ToArray(),
                DeduplicationKey: normalized.DeduplicationKey,
                CorrelationKey: normalized.CorrelationKey,
                ConcurrencyKey: normalized.ConcurrencyKey,
                Lane: normalized.Lane,
                PriorityCode: normalized.Priority is { } priority ? (byte)priority : null,
                // Normalize to UTC exactly as the owned enqueue path does (DbParams.Coerce): a Local/Unspecified
                // instant would otherwise persist wall-clock as UTC (mssql/sqlite) or be rejected (PG timestamptz).
                NextRunAtUtc: normalized.NextRunAtUtc is { } next ? DbParams.ToUtc(next) : null,
                DelaySeconds: normalized.DelaySeconds,
                TenantName: normalized.TenantName,
                Meta: OutboxMetaWriter.Write(normalized.Tags)
            );
    }
}

/// <summary>
/// Writes the validated <see cref="TagInput"/> list into the outbox <c>meta</c> column JSON, in the exact
/// documented shape the relay's <see cref="OutboxMetaReader"/> reads: a root object with an optional ordered
/// <c>tags</c> array of <c>{"name":..,"value":..}</c> entries. Names and values are camel-cased; a
/// presence-only tag writes its value as an explicit JSON <c>null</c>. No tags means a null <c>meta</c>
/// column. Source-generated and reflection-free per the repo AOT policy; the DTO pair and context are the
/// single definition shared with the reader.
/// </summary>
internal static class OutboxMetaWriter
{
    public static string? Write(IReadOnlyList<TagInput>? tags)
    {
        if (tags is null || tags.Count == 0)
        {
            return null;
        }

        var entries = new List<OutboxTagJson>(tags.Count);
        foreach (var tag in tags)
        {
            entries.Add(new OutboxTagJson(tag.Name, tag.Value));
        }

        return JsonSerializer.Serialize(new OutboxMetaJson(entries), OutboxMetaJsonContext.Default.OutboxMetaJson);
    }
}
