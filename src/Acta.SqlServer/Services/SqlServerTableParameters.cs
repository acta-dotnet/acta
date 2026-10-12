using System.Data;
using Microsoft.Data.SqlClient.Server;

namespace Acta.SqlServer;

/// <summary>
/// The table-valued parameters SqlServerDialect binds: each TVP's column shape, matching its CREATE TYPE in M001,
/// and the builder that streams one record per row.
/// </summary>
internal static class SqlServerTableParameters
{
    /// <summary>
    /// Column order and types must match the enqueue_jobs_batch TVP.
    /// </summary>
    internal static readonly SqlMetaData[] BatchColumns =
    [
        new("ordinal", SqlDbType.Int),
        new("job_ref", SqlDbType.UniqueIdentifier),
        new("namespace_name", SqlDbType.VarChar, 128),
        new("job_name", SqlDbType.VarChar, 128),
        new("deduplication_key", SqlDbType.VarChar, 128),
        new("correlation_key", SqlDbType.VarChar, 64),
        new("priority_override", SqlDbType.TinyInt),
        new("input_format_id", SqlDbType.TinyInt),
        new("input", SqlDbType.VarBinary, -1),
        new("concurrency_key", SqlDbType.VarChar, 128),
        new("next_run_at_utc", SqlDbType.DateTime2),
        new("delay_seconds", SqlDbType.Int),
        new("parent_id", SqlDbType.BigInt),
        new("tenant_name", SqlDbType.VarChar, 128),
        new("tenant_override", SqlDbType.Bit),
        new("lane", SqlDbType.VarChar, 128),
    ];

    internal static IEnumerable<SqlDataRecord> BuildBatchRecords(IReadOnlyList<JobEnqueueValues> rows, IReadOnlyList<Guid> jobRefs)
    {
        var record = new SqlDataRecord(BatchColumns);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            record.SetInt32(0, i);
            record.SetGuid(1, jobRefs[i]);
            record.SetString(2, row.NamespaceName);
            record.SetString(3, row.JobName);
            SetNullableString(record, 4, row.DeduplicationKey);
            SetNullableString(record, 5, row.CorrelationKey);
            if (row.PriorityOverride is { } priority)
            {
                record.SetByte(6, (byte)priority);
            }
            else
            {
                record.SetDBNull(6);
            }
            record.SetByte(7, row.Input.Format.Id);
            SetBytesOrNull(record, 8, row.Input.Data, !row.Input.Format.IsNone);
            SetNullableString(record, 9, row.ConcurrencyKey);
            SetNullableDateTime(record, 10, row.NextRunAtUtc is { } nextRun ? DbParams.ToUtc(nextRun) : null);
            SetNullableInt32(record, 11, row.DelaySeconds);
            if (row.ParentId is { } parentId)
            {
                record.SetInt64(12, parentId);
            }
            else
            {
                record.SetDBNull(12);
            }
            SetNullableString(record, 13, row.TenantName);
            record.SetBoolean(14, row.OverrideParentTenant);
            SetNullableString(record, 15, row.Lane);
            yield return record;
        }
    }

    internal static readonly SqlMetaData[] TagColumns =
    [
        new("ordinal", SqlDbType.Int),
        new("name", SqlDbType.VarChar, 128),
        new("value", SqlDbType.NVarChar, 128),
        new("value_search", SqlDbType.NVarChar, TagValueSearch.MaxLength),
    ];

    internal static IEnumerable<SqlDataRecord>? BuildTagRecords(IReadOnlyList<JobEnqueueValues> rows)
    {
        return !rows.Any(row => row.Tags is { Count: > 0 }) ? null : Stream(rows);

        static IEnumerable<SqlDataRecord> Stream(IReadOnlyList<JobEnqueueValues> rows)
        {
            var record = new SqlDataRecord(TagColumns);
            for (var i = 0; i < rows.Count; i++)
            {
                if (rows[i].Tags is not { Count: > 0 } tags)
                {
                    continue;
                }

                foreach (var tag in tags)
                {
                    record.SetInt32(0, i);
                    record.SetString(1, tag.Name);
                    SetNullableString(record, 2, tag.Value);
                    SetNullableString(record, 3, TagValueSearch.Normalize(tag.Value));
                    yield return record;
                }
            }
        }
    }

    internal static readonly SqlMetaData[] CompleteExecutionsColumns =
    [
        new("ordinal", SqlDbType.Int),
        new("job_id", SqlDbType.BigInt),
        new("execution_number", SqlDbType.Int),
        new("succeeded", SqlDbType.Bit),
        new("result_format_id", SqlDbType.TinyInt),
        new("result", SqlDbType.VarBinary, -1),
        new("reason_code", SqlDbType.TinyInt),
        new("reason_message", SqlDbType.NVarChar, 512),
        new("duration_ms", SqlDbType.Int),
        new("failure_count", SqlDbType.Int),
        new("retention_seconds", SqlDbType.Int),
        new("reschedule_status_code", SqlDbType.TinyInt),
        new("reschedule_delay_seconds", SqlDbType.Int),
        new("reschedule_resume_at_utc", SqlDbType.DateTime2),
        new("wait_signal_name", SqlDbType.NVarChar, 128),
        new("handler_status_code", SqlDbType.TinyInt),
        new("final_status", SqlDbType.TinyInt),
        new("job_next_run_at_utc", SqlDbType.DateTime2),
        new("recurring_result_cap", SqlDbType.Int),
    ];

    internal static IEnumerable<SqlDataRecord> BuildCompleteExecutionsRecords(IReadOnlyList<CompleteExecutionsValues> completions)
    {
        var record = new SqlDataRecord(CompleteExecutionsColumns);
        for (var i = 0; i < completions.Count; i++)
        {
            var completion = completions[i];
            record.SetInt32(0, i);
            record.SetInt64(1, completion.JobId);
            record.SetInt32(2, completion.ExecutionNumber);
            record.SetBoolean(3, completion.Succeeded);
            record.SetByte(4, completion.ResultFormatId);
            // A result with a format and no bytes is still a result, bound zero-length: results.result is NOT NULL.
            SetBytesOrNull(record, 5, completion.Result, completion.ResultFormatId != JobPayloadFormat.None.Id);
            SetNullableByte(record, 6, (byte?)completion.EventReasonCode);
            SetNullableString(record, 7, completion.ReasonMessage);
            SetNullableInt32(record, 8, completion.DurationMs);
            SetNullableInt32(record, 9, completion.FailureCount);
            SetNullableInt32(record, 10, completion.RetentionSeconds);
            SetNullableByte(record, 11, completion.RescheduleStatusCode);
            SetNullableInt32(record, 12, completion.RescheduleDelaySeconds);
            SetNullableDateTime(record, 13, completion.RescheduleResumeAtUtc);
            SetNullableString(record, 14, completion.WaitSignalName);
            SetNullableByte(record, 15, completion.HandlerStatusCode);
            SetNullableByte(record, 16, (byte?)completion.FinalStatus);
            SetNullableDateTime(record, 17, completion.JobNextRunAtUtc);
            SetNullableInt32(record, 18, completion.RecurringResultCap);
            yield return record;
        }
    }

    /// <summary>
    /// Column order must match the register_definitions_batch TVP.
    /// </summary>
    internal static readonly SqlMetaData[] DefinitionColumns =
    [
        new("name", SqlDbType.VarChar, 128),
        new("priority_code", SqlDbType.TinyInt),
        new("max_attempts", SqlDbType.Int),
        new("concurrency_limit", SqlDbType.Int),
        new("rate_limit", SqlDbType.VarChar, 16),
        new("rate_name", SqlDbType.VarChar, 128),
        new("backoff", SqlDbType.NVarChar, 64),
        new("execution_timeout_seconds", SqlDbType.Int),
        new("deadline_seconds", SqlDbType.Int),
        new("deadline_behavior_code", SqlDbType.TinyInt),
        new("retention_seconds", SqlDbType.Int),
        new("input_type_name", SqlDbType.VarChar, 512),
        new("output_type_name", SqlDbType.VarChar, 512),
        new("input_format_id", SqlDbType.TinyInt),
        new("input_format_name", SqlDbType.VarChar, 128),
        new("output_format_id", SqlDbType.TinyInt),
        new("output_format_name", SqlDbType.VarChar, 128),
        new("audit_level_code", SqlDbType.TinyInt),
        new("alert_profile_code", SqlDbType.TinyInt),
        new("alert_channel_name", SqlDbType.VarChar, 128),
        new("runbook_url", SqlDbType.VarChar, 512),
        new("display_name", SqlDbType.NVarChar, 128),
        new("description", SqlDbType.NVarChar, 512),
        new("definition_hash", SqlDbType.VarChar, 128),
        new("tenant_requirement_code", SqlDbType.TinyInt),
        new("lane", SqlDbType.VarChar, 128),
    ];

    internal static IEnumerable<SqlDataRecord>? BuildDefinitionRecords(IReadOnlyList<DefinitionValues> rows)
    {
        return rows.Count == 0 ? null : Stream(rows);

        static IEnumerable<SqlDataRecord> Stream(IReadOnlyList<DefinitionValues> rows)
        {
            var record = new SqlDataRecord(DefinitionColumns);
            foreach (var row in rows)
            {
                record.SetString(0, row.Name);
                record.SetByte(1, row.PriorityCode);
                record.SetInt32(2, row.MaxAttempts);
                SetNullableInt32(record, 3, row.ConcurrencyLimit);
                SetNullableString(record, 4, row.RateLimit);
                SetNullableString(record, 5, row.RateName);
                record.SetString(6, row.Backoff);
                record.SetInt32(7, row.ExecutionTimeoutSeconds);
                record.SetInt32(8, row.DeadlineSeconds);
                record.SetByte(9, row.DeadlineBehaviorCode);
                record.SetInt32(10, row.JobRetentionSeconds);
                record.SetString(11, row.InputTypeName);
                SetNullableString(record, 12, row.OutputTypeName);
                record.SetByte(13, row.InputFormatId);
                record.SetString(14, row.InputFormatName);
                record.SetByte(15, row.OutputFormatId);
                record.SetString(16, row.OutputFormatName);
                record.SetByte(17, row.AuditLevelCode);
                record.SetByte(18, row.AlertProfileCode);
                SetNullableString(record, 19, row.AlertChannelName);
                SetNullableString(record, 20, row.RunbookUrl);
                SetNullableString(record, 21, row.DisplayName);
                SetNullableString(record, 22, row.Description);
                record.SetString(23, row.DefinitionHash);
                record.SetByte(24, row.TenantRequirementCode);
                SetNullableString(record, 25, row.Lane);
                yield return record;
            }
        }
    }

    internal static readonly SqlMetaData[] SlotColumns =
    [
        new("definition_id", SqlDbType.Int),
        new("job_ref", SqlDbType.UniqueIdentifier),
        new("deduplication_key", SqlDbType.VarChar, 128),
        new("input_format_id", SqlDbType.TinyInt),
        new("input", SqlDbType.VarBinary, -1),
        new("audit_level_code", SqlDbType.TinyInt),
        new("slot_status_code", SqlDbType.TinyInt),
        new("slot_next_run_at_utc", SqlDbType.DateTime2),
    ];

    internal static IEnumerable<SqlDataRecord>? BuildSlotRecords(
        IReadOnlyList<DefinitionSchedules> definitions,
        IReadOnlyList<Guid> slotRefs
    )
    {
        return definitions.Count == 0 ? null : Stream(definitions, slotRefs);

        static IEnumerable<SqlDataRecord> Stream(IReadOnlyList<DefinitionSchedules> definitions, IReadOnlyList<Guid> slotRefs)
        {
            var record = new SqlDataRecord(SlotColumns);
            for (var i = 0; i < definitions.Count; i++)
            {
                var definition = definitions[i];
                record.SetInt32(0, definition.DefinitionId);
                record.SetGuid(1, slotRefs[i]);
                record.SetString(2, definition.JobName);
                record.SetByte(3, definition.InputFormatId);
                SetBytesOrNull(record, 4, definition.Input, !definition.Input.IsEmpty);
                record.SetByte(5, (byte)definition.AuditLevel);
                record.SetByte(6, (byte)definition.SlotStatus);
                SetNullableDateTime(record, 7, definition.SlotMinNextRunAtUtc);
                yield return record;
            }
        }
    }

    internal static readonly SqlMetaData[] ScheduleUpsertColumns =
    [
        new("definition_id", SqlDbType.Int),
        new("name", SqlDbType.VarChar, 128),
        new("expression", SqlDbType.VarChar, 128),
        new("time_zone_id", SqlDbType.VarChar, 128),
        new("expression_kind_code", SqlDbType.TinyInt),
        new("misfire_strategy_code", SqlDbType.TinyInt),
        new("next_run_at_utc", SqlDbType.DateTime2),
        new("description", SqlDbType.NVarChar, 512),
    ];

    internal static IEnumerable<SqlDataRecord>? BuildScheduleUpsertRecords(IReadOnlyList<DefinitionSchedules> definitions)
    {
        return !definitions.Any(definition => definition.Schedules.Any()) ? null : Stream(definitions);

        static IEnumerable<SqlDataRecord> Stream(IReadOnlyList<DefinitionSchedules> definitions)
        {
            var record = new SqlDataRecord(ScheduleUpsertColumns);
            foreach (var definition in definitions)
            {
                foreach (var schedule in definition.Schedules)
                {
                    record.SetInt32(0, definition.DefinitionId);
                    record.SetString(1, schedule.Name);
                    record.SetString(2, schedule.Expression);
                    record.SetString(3, string.IsNullOrWhiteSpace(schedule.TimeZoneId) ? "UTC" : schedule.TimeZoneId);
                    record.SetByte(4, (byte)schedule.ExpressionKind);
                    record.SetByte(5, (byte)schedule.MisfireStrategy);
                    SetNullableDateTime(record, 6, schedule.NextRunAtUtc);
                    SetNullableString(record, 7, schedule.Description);
                    yield return record;
                }
            }
        }
    }

    /// <summary>
    /// The TVP CREATE TYPE bodies live in M001 (emitted from tools/Acta.Emit's SqlServerDdlDialect),
    /// while the SqlDataRecord shapes bind positionally against them here; TvpParityTests compares the
    /// two through this map so a column added on one side fails a unit test, not a live DB apply.
    /// Declared last: a static initializer reading the arrays above must run after them.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, SqlMetaData[]> TvpShapes = new Dictionary<string, SqlMetaData[]>(
        StringComparer.Ordinal
    )
    {
        ["enqueue_jobs_batch"] = BatchColumns,
        ["enqueue_tags_batch"] = TagColumns,
        ["complete_executions_batch"] = CompleteExecutionsColumns,
        ["register_definitions_batch"] = DefinitionColumns,
        ["register_slots_batch"] = SlotColumns,
        ["register_schedules_batch"] = ScheduleUpsertColumns,
    };

    /// <summary>
    /// Nullable-aware <see cref="SqlDataRecord"/> setters the dialect's TVP binders use. A TVP record is
    /// reused across rows, so every field must be written on every row: leaving one unset carries the
    /// previous row's value forward.
    /// </summary>
    internal static void SetNullableString(SqlDataRecord record, int ordinal, string? value)
    {
        if (value is null)
        {
            record.SetDBNull(ordinal);
        }
        else
        {
            record.SetString(ordinal, value);
        }
    }

    internal static void SetNullableByte(SqlDataRecord record, int ordinal, byte? value)
    {
        if (value is { } number)
        {
            record.SetByte(ordinal, number);
        }
        else
        {
            record.SetDBNull(ordinal);
        }
    }

    internal static void SetNullableInt32(SqlDataRecord record, int ordinal, int? value)
    {
        if (value is { } number)
        {
            record.SetInt32(ordinal, number);
        }
        else
        {
            record.SetDBNull(ordinal);
        }
    }

    internal static void SetNullableDateTime(SqlDataRecord record, int ordinal, DateTime? value)
    {
        if (value is { } instant)
        {
            record.SetDateTime(ordinal, instant);
        }
        else
        {
            record.SetDBNull(ordinal);
        }
    }

    /// <summary>
    /// SetValue replaces the whole field; SetBytes against a reused record can retain a prior payload tail.
    /// </summary>
    internal static void SetBytesOrNull(SqlDataRecord record, int ordinal, ReadOnlyMemory<byte> data, bool present)
    {
        if (!present)
        {
            record.SetDBNull(ordinal);
            return;
        }

        record.SetValue(ordinal, data.ToArray());
    }
}
