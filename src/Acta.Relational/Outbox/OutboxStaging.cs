using System.Data.Common;
using System.Text.Json;
using Acta;
using Acta.Features.Outbox;
using Acta.Relational.Commands;
using Acta.Relational.Connections;

namespace Acta.Relational.Outbox;

/// <summary>
/// The EF-free staging core the provider packages consume. Normalizes and validates a raw
/// <see cref="JobEnqueueRequest"/> through the shared enqueue validation from <c>Acta.Contracts</c>
/// (identifiers, tags, schedule mutual exclusion, priority, payload pair) and layers the two stricter
/// outbox-only rules on top: a deduplication key is required and a parent id is rejected. A valid request
/// projects into an <see cref="OutboxStagingRow"/> with a client-generated <c>outbox_id</c>. <see cref="Prepare"/>
/// runs the whole provider-neutral prologue (identifier qualification, request projection, transaction
/// guard, INSERT text), leaving each provider extension only its typed parameter binding and execution.
/// </summary>
internal static class OutboxStaging
{
    // The canonical INSERT column list and VALUES clause, byte-identical across every provider. The provider
    // extension binds these named parameters with its own typed parameter objects.
    private const string InsertColumnsAndValues = """
            (outbox_id, job_namespace, job_name, input_format_id, input_data, deduplication_key,
             correlation_key, exclusive_key, priority_code, next_run_at_utc, delay_seconds, tenant_key, meta)
        VALUES (@outbox_id, @job_namespace, @job_name, @input_format_id, @input_data, @deduplication_key,
                @correlation_key, @exclusive_key, @priority_code, @next_run_at_utc, @delay_seconds, @tenant_key, @meta);
        """;

    /// <summary>
    /// The provider-neutral staging prologue: validate/qualify the table reference, validate and project the
    /// request into a row, and structurally guard the caller transaction. Returns the row, the transaction's
    /// open connection, and the full canonical INSERT text. Runs before any I/O, so a bad request or a
    /// detached/closed transaction throws exactly like the transactional enqueue overloads.
    /// </summary>
    public static (OutboxStagingRow Row, DbConnection Connection, string Sql) Prepare(
        DbTransaction transaction,
        JobEnqueueRequest request,
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

    public static OutboxStagingRow Stage(JobEnqueueRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalized = JobEnqueueRequestValidation.NormalizeAndValidate(request, nameof(request));

        if (normalized.DeduplicationKey is null)
        {
            throw new ArgumentException(
                "An outbox record requires a non-null DeduplicationKey.",
                $"{nameof(request)}.{nameof(JobEnqueueRequest.DeduplicationKey)}"
            );
        }

        if (normalized.ParentId is not null)
        {
            throw new ArgumentException(
                "An outbox record cannot carry a ParentId; external records request root jobs only.",
                $"{nameof(request)}.{nameof(JobEnqueueRequest.ParentId)}"
            );
        }

        return new OutboxStagingRow(
            OutboxId: Guid.NewGuid(),
            JobNamespace: normalized.JobNamespace,
            JobName: normalized.JobName,
            InputFormatId: normalized.Input.Format.Id,
            InputData: normalized.Input.IsNone ? null : normalized.Input.Data.ToArray(),
            DeduplicationKey: normalized.DeduplicationKey,
            CorrelationKey: normalized.CorrelationKey,
            ExclusiveKey: normalized.ExclusiveKey,
            PriorityCode: normalized.Priority is { } priority ? (byte)priority : null,
            // Normalize to UTC exactly as the owned enqueue path does (DbParams.Coerce): a Local/Unspecified
            // instant would otherwise persist wall-clock as UTC (mssql/sqlite) or be rejected (PG timestamptz).
            NextRunAtUtc: normalized.NextRunAtUtc is { } next ? DbParams.ToUtc(next) : null,
            DelaySeconds: normalized.DelaySeconds,
            TenantKey: normalized.TenantKey,
            Meta: OutboxMetaWriter.Write(normalized.Tags)
        );
    }
}

/// <summary>
/// The canonical, caller-supplied columns of one staged external-outbox row, projected from a validated
/// <see cref="JobEnqueueRequest"/> by <see cref="OutboxStaging"/>. Carries only what the producer
/// writes: <c>outbox_id</c> is the client-generated GUID and the operational columns
/// (<c>created_at_utc</c>, <c>status_code</c>, <c>failure_count</c>, <c>next_attempt_at_utc</c>, the claim
/// pair, <c>last_error</c>) are omitted so the database defaults apply. Each provider staging extension
/// binds these fields into its own canonical INSERT.
/// </summary>
internal readonly record struct OutboxStagingRow(
    Guid OutboxId,
    string JobNamespace,
    string JobName,
    byte InputFormatId,
    byte[]? InputData,
    string DeduplicationKey,
    string? CorrelationKey,
    string? ExclusiveKey,
    byte? PriorityCode,
    DateTime? NextRunAtUtc,
    int? DelaySeconds,
    string? TenantKey,
    string? Meta
);

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

        var entries = new List<OutboxTagDto>(tags.Count);
        foreach (var tag in tags)
        {
            entries.Add(new OutboxTagDto(tag.Name, tag.Value));
        }

        return JsonSerializer.Serialize(new OutboxMetaDto(entries), OutboxMetaJsonContext.Default.OutboxMetaDto);
    }
}
