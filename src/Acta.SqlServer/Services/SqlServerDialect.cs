using System.Data;
using System.Data.Common;
using Acta.Configuration;
using Acta.Features.Definitions;
using Acta.Features.Execution;
using Acta.Features.Jobs;
using Acta.Features.Schedules;
using Acta.Relational.Commands;
using Acta.Relational.Connections;
using Acta.Relational.Schema;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlClient.Server;

namespace Acta.SqlServer.Services;

/// <summary>
/// SQL Server <see cref="ISqlDialect"/>: connection creation, generic parameter coercion, and
/// routine invocation. Feature stores own their command shapes and bind provider parameters directly.
/// </summary>
internal sealed class SqlServerDialect : ISqlDialect
{
    public DbProvider Provider => DbProvider.SqlServer;

    public string DialectToken => "mssql";

    public bool SupportsRoutines => true;

    // 1205: deadlock victim. 2801: an installed routine changed during concurrent bootstrap.
    public bool IsTransientConflict(Exception exception) => exception is SqlException { Number: 1205 or 2801 };

    // A token-cancelled command surfaces as SqlException Number 0 ("A severe error occurred on the
    // current command") at severity Class 11, the attention signal. 3980 ("the batch is aborted" by
    // a client abort signal) is an unambiguous cancellation code. Number 0 is SqlClient's catch-all
    // for a severe error, so a connection-fatal fault (KILL, transport reset) can also carry it, but
    // at Class 20+; excluding the fatal classes keeps a genuine transport fault racing a cancelled
    // token surfacing as the error it is. The retry funnel consults this only under a cancelled token.
    public bool IsCancellation(Exception exception) =>
        exception is SqlException { Number: 3980 } or SqlException { Number: 0, Class: < 20 };

    public DbConnection CreateConnection(string connectionString) => new SqlConnection(connectionString);

    public bool OwnsConnection(DbConnection connection) => connection is SqlConnection;

    public DbParameter CreateParameter(DbParameterSpec spec)
    {
        DbParams.Validate(spec);
        var parameter = new SqlParameter { ParameterName = "@" + spec.Name, SqlDbType = MapKind(spec) };

        if (spec.Size is { } size)
        {
            parameter.Size = size;
        }
        if (spec.Precision is { } precision)
        {
            parameter.Precision = (byte)precision;
        }
        if (spec.Scale is { } scale)
        {
            parameter.Scale = (byte)scale;
        }

        parameter.Value = DbParams.Coerce(spec);
        return parameter;
    }

    public void ConfigureRoutineCommand(DbCommand command, string schema, string routineName)
    {
        IdentifierSyntax.ValidateBareIdentifier(routineName, nameof(routineName));
        command.CommandText = $"{schema}.{routineName}";
        command.CommandType = CommandType.StoredProcedure;
    }

    private static SqlDbType MapKind(DbParameterSpec parameter) =>
        parameter.Kind switch
        {
            DbKind.Boolean => SqlDbType.Bit,
            DbKind.Byte => SqlDbType.TinyInt,
            DbKind.Int16 => SqlDbType.SmallInt,
            DbKind.Int32 => SqlDbType.Int,
            DbKind.Int64 => SqlDbType.BigInt,
            DbKind.Guid => SqlDbType.UniqueIdentifier,
            DbKind.UtcInstant => SqlDbType.DateTime2,
            DbKind.Decimal => SqlDbType.Decimal,
            DbKind.AsciiString => SqlDbType.VarChar,
            DbKind.UnicodeString => SqlDbType.NVarChar,
            DbKind.Bytes => SqlDbType.VarBinary,
            DbKind.BinaryPayload => SqlDbType.VarBinary,
            _ => throw new InvalidOperationException($"Unmapped DbKind '{parameter.Kind}' for SqlServer parameter '{parameter.Name}'."),
        };

    public void BindEnqueueBatch(DbCommand command, IReadOnlyList<JobEnqueueRow> rows, IReadOnlyList<Guid> jobRefs, string schema)
    {
        var sql = (SqlCommand)command;
        sql.Parameters.Add(
            new SqlParameter
            {
                ParameterName = "@p_batch",
                SqlDbType = SqlDbType.Structured,
                TypeName = $"{schema}.job_enqueue_batch",
                Value = BuildBatchRecords(rows, jobRefs),
            }
        );
        sql.Parameters.Add(
            new SqlParameter
            {
                ParameterName = "@p_tag_batch",
                SqlDbType = SqlDbType.Structured,
                TypeName = $"{schema}.job_enqueue_tag_batch",
                Value = BuildTagRecords(rows),
            }
        );
    }

    public void BindEnqueueOne(DbCommand command, JobEnqueueRow row, Guid jobRef, string schema)
    {
        var sql = (SqlCommand)command;
        AddScalar(sql, "@p_job_ref", SqlDbType.UniqueIdentifier, jobRef);
        AddScalar(sql, "@p_namespace_name", SqlDbType.VarChar, row.NamespaceName);
        AddScalar(sql, "@p_job_name", SqlDbType.VarChar, row.JobName);
        AddScalar(sql, "@p_deduplication_key", SqlDbType.VarChar, (object?)row.DeduplicationKey ?? DBNull.Value);
        AddScalar(sql, "@p_correlation_key", SqlDbType.VarChar, (object?)row.CorrelationKey ?? DBNull.Value);
        AddScalar(sql, "@p_priority_override", SqlDbType.TinyInt, row.PriorityOverride is { } priority ? (byte)priority : DBNull.Value);
        AddScalar(sql, "@p_input_format_id", SqlDbType.TinyInt, row.Input.Format.Id);
        AddScalar(sql, "@p_input", SqlDbType.VarBinary, row.Input.Format.IsNone ? DBNull.Value : row.Input.Data.ToArray());
        AddScalar(sql, "@p_exclusive_key", SqlDbType.VarChar, (object?)row.ExclusiveKey ?? DBNull.Value);
        AddScalar(sql, "@p_next_run_at_utc", SqlDbType.DateTime2, row.NextRunAtUtc is { } nextRun ? DbParams.ToUtc(nextRun) : DBNull.Value);
        AddScalar(sql, "@p_delay_seconds", SqlDbType.Int, (object?)row.DelaySeconds ?? DBNull.Value);
        AddScalar(sql, "@p_parent_id", SqlDbType.BigInt, (object?)row.ParentId ?? DBNull.Value);
        AddScalar(sql, "@p_tenant_key", SqlDbType.VarChar, (object?)row.TenantKey ?? DBNull.Value);
        AddScalar(sql, "@p_tenant_override", SqlDbType.Bit, row.OverrideParentTenant);
        sql.Parameters.Add(
            new SqlParameter
            {
                ParameterName = "@p_tag_batch",
                SqlDbType = SqlDbType.Structured,
                TypeName = $"{schema}.job_enqueue_tag_batch",
                Value = BuildTagRecords([row]),
            }
        );
    }

    private static void AddScalar(SqlCommand command, string name, SqlDbType type, object value) =>
        command.Parameters.Add(
            new SqlParameter
            {
                ParameterName = name,
                SqlDbType = type,
                Value = value,
            }
        );

    // Column order and types must match the job_enqueue_batch TVP.
    private static readonly SqlMetaData[] BatchColumns =
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
        new("exclusive_key", SqlDbType.VarChar, 128),
        new("next_run_at_utc", SqlDbType.DateTime2),
        new("delay_seconds", SqlDbType.Int),
        new("parent_id", SqlDbType.BigInt),
        new("tenant_key", SqlDbType.VarChar, 128),
        new("tenant_override", SqlDbType.Bit),
    ];

    private static IEnumerable<SqlDataRecord> BuildBatchRecords(IReadOnlyList<JobEnqueueRow> rows, IReadOnlyList<Guid> jobRefs)
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
            SetNullableString(record, 9, row.ExclusiveKey);
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
            SetNullableString(record, 13, row.TenantKey);
            record.SetBoolean(14, row.OverrideParentTenant);
            yield return record;
        }
    }

    private static readonly SqlMetaData[] TagColumns =
    [
        new("ordinal", SqlDbType.Int),
        new("name", SqlDbType.VarChar, 128),
        new("value", SqlDbType.NVarChar, 128),
        new("value_search", SqlDbType.NVarChar, TagValueSearch.MaxLength),
    ];

    private static IEnumerable<SqlDataRecord>? BuildTagRecords(IReadOnlyList<JobEnqueueRow> rows)
    {
        if (!rows.Any(row => row.Tags is { Count: > 0 }))
        {
            return null;
        }

        return Stream(rows);

        static IEnumerable<SqlDataRecord> Stream(IReadOnlyList<JobEnqueueRow> rows)
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

    public void BindRegisterJobDefinitions(
        DbCommand command,
        short namespaceId,
        DateTime manifestGenerationUtc,
        IReadOnlyList<JobDefinitionRow> rows,
        string schema
    )
    {
        var sql = (SqlCommand)command;
        sql.Parameters.Add(new SqlParameter("@p_namespace_id", SqlDbType.SmallInt) { Value = namespaceId });
        sql.Parameters.Add(new SqlParameter("@p_manifest_generation", SqlDbType.DateTime2) { Value = manifestGenerationUtc });
        sql.Parameters.Add(
            new SqlParameter
            {
                ParameterName = "@p_definitions",
                SqlDbType = SqlDbType.Structured,
                TypeName = $"{schema}.job_definition_batch",
                Value = BuildDefinitionRecords(rows),
            }
        );
    }

    // Column order must match the job_definition_batch TVP.
    private static readonly SqlMetaData[] DefinitionColumns =
    [
        new("name", SqlDbType.VarChar, 128),
        new("priority_code", SqlDbType.TinyInt),
        new("max_attempts", SqlDbType.SmallInt),
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
    ];

    private static IEnumerable<SqlDataRecord>? BuildDefinitionRecords(IReadOnlyList<JobDefinitionRow> rows)
    {
        if (rows.Count == 0)
        {
            return null;
        }

        return Stream(rows);

        static IEnumerable<SqlDataRecord> Stream(IReadOnlyList<JobDefinitionRow> rows)
        {
            var record = new SqlDataRecord(DefinitionColumns);
            foreach (var row in rows)
            {
                record.SetString(0, row.Name);
                record.SetByte(1, row.PriorityCode);
                record.SetInt16(2, row.MaxAttempts);
                record.SetString(3, row.Backoff);
                record.SetInt32(4, row.ExecutionTimeoutSeconds);
                record.SetInt32(5, row.DeadlineSeconds);
                record.SetByte(6, row.DeadlineBehaviorCode);
                record.SetInt32(7, row.JobRetentionSeconds);
                record.SetString(8, row.InputTypeName);
                SetNullableString(record, 9, row.OutputTypeName);
                record.SetByte(10, row.InputFormatId);
                record.SetString(11, row.InputFormatName);
                record.SetByte(12, row.OutputFormatId);
                record.SetString(13, row.OutputFormatName);
                record.SetByte(14, row.AuditLevelCode);
                record.SetByte(15, row.AlertProfileCode);
                SetNullableString(record, 16, row.AlertChannelName);
                SetNullableString(record, 17, row.RunbookUrl);
                SetNullableString(record, 18, row.DisplayName);
                SetNullableString(record, 19, row.Description);
                record.SetString(20, row.DefinitionHash);
                record.SetByte(21, row.TenantRequirementCode);
                yield return record;
            }
        }
    }

    public void BindRegisterScheduledJobs(
        DbCommand command,
        IReadOnlyList<DefinitionSchedules> definitions,
        IReadOnlyList<Guid> slotRefs,
        string schema
    )
    {
        var sql = (SqlCommand)command;
        sql.Parameters.Add(new SqlParameter("@p_namespace_id", SqlDbType.SmallInt) { Value = definitions[0].NamespaceId });
        sql.Parameters.Add(
            new SqlParameter
            {
                ParameterName = "@p_definitions",
                SqlDbType = SqlDbType.Structured,
                TypeName = $"{schema}.job_schedule_slot_batch",
                Value = BuildSlotRecords(definitions, slotRefs),
            }
        );
        sql.Parameters.Add(
            new SqlParameter
            {
                ParameterName = "@p_schedules",
                SqlDbType = SqlDbType.Structured,
                TypeName = $"{schema}.job_schedule_upsert_batch",
                Value = BuildScheduleUpsertRecords(definitions),
            }
        );
    }

    private static readonly SqlMetaData[] SlotColumns =
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

    private static IEnumerable<SqlDataRecord>? BuildSlotRecords(
        IReadOnlyList<DefinitionSchedules> definitions,
        IReadOnlyList<Guid> slotRefs
    )
    {
        if (definitions.Count == 0)
        {
            return null;
        }

        return Stream(definitions, slotRefs);

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

    private static readonly SqlMetaData[] ScheduleUpsertColumns =
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

    private static IEnumerable<SqlDataRecord>? BuildScheduleUpsertRecords(IReadOnlyList<DefinitionSchedules> definitions)
    {
        if (!definitions.Any(definition => definition.Schedules.Any()))
        {
            return null;
        }

        return Stream(definitions);

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
                    record.SetString(3, string.IsNullOrWhiteSpace(schedule.TimeZone) ? "UTC" : schedule.TimeZone);
                    record.SetByte(4, (byte)schedule.ExpressionKind);
                    record.SetByte(5, (byte)schedule.Misfire);
                    SetNullableDateTime(record, 6, schedule.NextRunAtUtc);
                    SetNullableString(record, 7, schedule.Description);
                    yield return record;
                }
            }
        }
    }

    public void BindRecurringCompletion(DbCommand command, CompleteExecutionRequest request, string schema)
    {
        var sql = (SqlCommand)command;
        var resultBytes = request.Result.IsEmpty ? [] : request.Result.ToArray();

        AddParameter(sql, "@p_id", SqlDbType.BigInt, request.JobId);
        AddParameter(sql, "@p_leased_by_worker_id", SqlDbType.Int, request.WorkerId);
        AddParameter(sql, "@p_execution_number", SqlDbType.Int, request.ExpectedExecutionNumber);
        AddParameter(sql, "@p_reason_code", SqlDbType.TinyInt, request.JobEventReasonCode is { } reason ? (byte)reason : DBNull.Value);
        AddParameter(sql, "@p_reason_message", SqlDbType.NVarChar, (object?)request.ReasonMessage ?? DBNull.Value, size: 512);
        AddParameter(sql, "@p_result_format_id", SqlDbType.TinyInt, request.ResultFormatId);
        AddParameter(sql, "@p_result", SqlDbType.VarBinary, resultBytes);
        AddParameter(sql, "@p_execution_succeeded", SqlDbType.Bit, request.Outcome == ExecutionOutcome.Succeeded);
        AddParameter(sql, "@p_duration_ms", SqlDbType.Int, request.DurationMs is { } duration ? duration : DBNull.Value);
        AddParameter(sql, "@p_reschedule_status_code", SqlDbType.TinyInt, DBNull.Value);
        AddParameter(sql, "@p_reschedule_delay_seconds", SqlDbType.Int, DBNull.Value);
        AddParameter(sql, "@p_reschedule_resume_at_utc", SqlDbType.DateTime2, DBNull.Value);
        AddParameter(sql, "@p_wait_signal_name", SqlDbType.NVarChar, DBNull.Value, size: 128);
        AddParameter(sql, "@p_handler_status_code", SqlDbType.TinyInt, DBNull.Value);
        AddParameter(sql, "@p_retention_seconds", SqlDbType.Int, request.RetentionSeconds is { } retention ? retention : DBNull.Value);
        AddParameter(sql, "@p_final_status", SqlDbType.TinyInt, (byte)request.FinalStatus!.Value);
        AddParameter(sql, "@p_job_next_run_at_utc", SqlDbType.DateTime2, request.JobNextRunAtUtc is { } nextRun ? nextRun : DBNull.Value);
        AddParameter(sql, "@p_failure_count", SqlDbType.SmallInt, request.FailureCount is { } failureCount ? failureCount : DBNull.Value);
        AddParameter(sql, "@p_recurring_result_cap", SqlDbType.Int, request.RecurringResultCap);
        AddParameter(
            sql,
            "@p_schedule_advances",
            SqlDbType.Structured,
            BuildScheduleAdvanceRecords(request.ScheduleAdvances)!,
            typeName: $"{schema}.job_schedule_advance_batch"
        );
    }

    public void BindCompleteExecutionsBatch(DbCommand command, IReadOnlyList<CompleteExecutionRequest> requests, string schema)
    {
        AddParameter(
            (SqlCommand)command,
            "@p_batch",
            SqlDbType.Structured,
            BuildCompleteBatchRecords(requests)!,
            typeName: $"{schema}.complete_executions_batch"
        );
    }

    private static void AddParameter(
        SqlCommand command,
        string name,
        SqlDbType type,
        object value,
        int? size = null,
        string? typeName = null
    )
    {
        var parameter = new SqlParameter
        {
            ParameterName = name,
            SqlDbType = type,
            Value = value,
        };
        if (size is { } parameterSize)
        {
            parameter.Size = parameterSize;
        }
        if (typeName is not null)
        {
            parameter.TypeName = typeName;
        }
        command.Parameters.Add(parameter);
    }

    private static readonly SqlMetaData[] CompleteBatchColumns =
    [
        new("ordinal", SqlDbType.Int),
        new("id", SqlDbType.BigInt),
        new("worker_id", SqlDbType.Int),
        new("execution_number", SqlDbType.Int),
        new("succeeded", SqlDbType.Bit),
        new("duration_ms", SqlDbType.Int),
        new("reason_code", SqlDbType.TinyInt),
        new("reason_message", SqlDbType.NVarChar, 512),
        new("result_format_id", SqlDbType.TinyInt),
        new("result", SqlDbType.VarBinary, -1),
        new("failure_count", SqlDbType.SmallInt),
        new("retention_seconds", SqlDbType.Int),
    ];

    private static IEnumerable<SqlDataRecord>? BuildCompleteBatchRecords(IReadOnlyList<CompleteExecutionRequest> requests)
    {
        if (requests.Count == 0)
        {
            return null;
        }

        return Stream(requests);

        static IEnumerable<SqlDataRecord> Stream(IReadOnlyList<CompleteExecutionRequest> requests)
        {
            var record = new SqlDataRecord(CompleteBatchColumns);
            for (var i = 0; i < requests.Count; i++)
            {
                var request = requests[i];
                record.SetInt32(0, i);
                record.SetInt64(1, request.JobId);
                record.SetInt32(2, request.WorkerId);
                record.SetInt32(3, request.ExpectedExecutionNumber);
                record.SetBoolean(4, request.Outcome == ExecutionOutcome.Succeeded);
                SetNullableInt32(record, 5, request.DurationMs);
                SetNullableInt16(record, 6, request.JobEventReasonCode is { } reason ? (short)reason : null);
                SetNullableString(record, 7, request.ReasonMessage);
                record.SetByte(8, request.ResultFormatId);
                SetBytesOrNull(record, 9, request.Result, !request.Result.IsEmpty);
                SetNullableInt16(record, 10, (short?)request.FailureCount);
                SetNullableInt32(record, 11, request.RetentionSeconds);
                yield return record;
            }
        }
    }

    private static readonly SqlMetaData[] ScheduleAdvanceColumns =
    [
        new("schedule_id", SqlDbType.BigInt),
        new("next_run_at_utc", SqlDbType.DateTime2),
    ];

    private static IEnumerable<SqlDataRecord>? BuildScheduleAdvanceRecords(IReadOnlyList<ScheduleAdvance>? advances)
    {
        if (advances is not { Count: > 0 })
        {
            return null;
        }

        return Stream(advances);

        static IEnumerable<SqlDataRecord> Stream(IReadOnlyList<ScheduleAdvance> advances)
        {
            var record = new SqlDataRecord(ScheduleAdvanceColumns);
            foreach (var advance in advances)
            {
                record.SetInt64(0, advance.ScheduleId);
                SetNullableDateTime(record, 1, advance.NextRunAtUtc);
                yield return record;
            }
        }
    }

    // The TVP CREATE TYPE bodies live in M001 (emitted from tools/Acta.Emit's SqlServerDdlDialect),
    // while the SqlDataRecord shapes bind positionally against them here; TvpParityTests compares the
    // two through this map so a column added on one side fails a unit test, not a live DB apply.
    // Declared last: a static initializer reading the arrays above must run after them.
    internal static readonly IReadOnlyDictionary<string, SqlMetaData[]> TvpShapes = new Dictionary<string, SqlMetaData[]>(
        StringComparer.Ordinal
    )
    {
        ["job_enqueue_batch"] = BatchColumns,
        ["job_enqueue_tag_batch"] = TagColumns,
        ["job_definition_batch"] = DefinitionColumns,
        ["job_schedule_slot_batch"] = SlotColumns,
        ["job_schedule_upsert_batch"] = ScheduleUpsertColumns,
        ["job_schedule_advance_batch"] = ScheduleAdvanceColumns,
        ["complete_executions_batch"] = CompleteBatchColumns,
    };

    /// <summary>
    /// Nullable-aware <see cref="SqlDataRecord"/> setters the dialect's TVP binders use. A TVP record is
    /// reused across rows, so every field must be written on every row: leaving one unset carries the
    /// previous row's value forward.
    /// </summary>
    private static void SetNullableString(SqlDataRecord record, int ordinal, string? value)
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

    private static void SetNullableInt16(SqlDataRecord record, int ordinal, short? value)
    {
        if (value is { } number)
        {
            record.SetInt16(ordinal, number);
        }
        else
        {
            record.SetDBNull(ordinal);
        }
    }

    private static void SetNullableInt32(SqlDataRecord record, int ordinal, int? value)
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

    private static void SetNullableDateTime(SqlDataRecord record, int ordinal, DateTime? value)
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

    // SetValue replaces the whole field; SetBytes against a reused record can retain a prior payload tail.
    private static void SetBytesOrNull(SqlDataRecord record, int ordinal, ReadOnlyMemory<byte> data, bool present)
    {
        if (!present)
        {
            record.SetDBNull(ordinal);
            return;
        }

        record.SetValue(ordinal, data.ToArray());
    }
}
