using System.Data;
using System.Data.Common;
using Acta.Runtime;
using Microsoft.Data.SqlClient;

namespace Acta.SqlServer;

/// <summary>
/// SQL Server <see cref="ISqlDialect"/>: connection creation, generic parameter coercion, and
/// routine invocation. Feature stores own their command shapes and bind provider parameters directly.
/// </summary>
internal sealed class SqlServerDialect : ISqlDialect
{
    public DbProvider Provider => DbProvider.SqlServer;

    public string DialectToken => "mssql";

    /// <summary>
    /// 1205: deadlock victim. 2801: an installed routine changed during concurrent bootstrap.
    /// </summary>
    public bool IsTransientConflict(Exception exception) => exception is SqlException { Number: 1205 or 2801 };

    /// <summary>
    /// A token-cancelled command surfaces as SqlException Number 0 ("A severe error occurred on the
    /// current command") at severity Class 11, the attention signal. 3980 ("the batch is aborted" by
    /// a client abort signal) is an unambiguous cancellation code. Number 0 is SqlClient's catch-all
    /// for a severe error, so a connection-fatal fault (KILL, transport reset) can also carry it, but
    /// at Class 20+; excluding the fatal classes keeps a genuine transport fault racing a cancelled
    /// token surfacing as the error it is. The retry funnel consults this only under a cancelled token.
    /// </summary>
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

    /// <summary>
    /// JSON array text the procedure expands with OPENJSON. An empty set binds NULL, which skips the
    /// expansion, leaves the count at zero, and so settles the claim's filter term before any
    /// <c>jobs</c> lookup.
    /// </summary>
    public void BindExcludedDefinitionIds(DbCommand command, int[]? definitionIds) =>
        AddScalar(
            (SqlCommand)command,
            "@p_excluded_definition_ids",
            SqlDbType.NVarChar,
            (object?)DbParams.JsonIdArray(definitionIds) ?? DBNull.Value
        );

    public void BindEnqueueJobs(DbCommand command, IReadOnlyList<JobEnqueueValues> rows, IReadOnlyList<Guid> jobRefs, string schema)
    {
        var sql = (SqlCommand)command;
        sql.Parameters.Add(
            new SqlParameter
            {
                ParameterName = "@p_batch",
                SqlDbType = SqlDbType.Structured,
                TypeName = $"{schema}.job_enqueue_batch",
                Value = SqlServerTableParameters.BuildBatchRecords(rows, jobRefs),
            }
        );
        sql.Parameters.Add(
            new SqlParameter
            {
                ParameterName = "@p_tag_batch",
                SqlDbType = SqlDbType.Structured,
                TypeName = $"{schema}.job_enqueue_tag_batch",
                Value = SqlServerTableParameters.BuildTagRecords(rows),
            }
        );
    }

    public void BindEnqueueJob(DbCommand command, JobEnqueueValues row, Guid jobRef, string schema)
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
        AddScalar(sql, "@p_concurrency_key", SqlDbType.VarChar, (object?)row.ConcurrencyKey ?? DBNull.Value);
        AddScalar(sql, "@p_next_run_at_utc", SqlDbType.DateTime2, row.NextRunAtUtc is { } nextRun ? DbParams.ToUtc(nextRun) : DBNull.Value);
        AddScalar(sql, "@p_delay_seconds", SqlDbType.Int, (object?)row.DelaySeconds ?? DBNull.Value);
        AddScalar(sql, "@p_parent_id", SqlDbType.BigInt, (object?)row.ParentId ?? DBNull.Value);
        AddScalar(sql, "@p_tenant_key", SqlDbType.VarChar, (object?)row.TenantKey ?? DBNull.Value);
        AddScalar(sql, "@p_tenant_override", SqlDbType.Bit, row.OverrideParentTenant);
        AddScalar(sql, "@p_lane", SqlDbType.VarChar, (object?)row.Lane ?? DBNull.Value);
        sql.Parameters.Add(
            new SqlParameter
            {
                ParameterName = "@p_tag_batch",
                SqlDbType = SqlDbType.Structured,
                TypeName = $"{schema}.job_enqueue_tag_batch",
                Value = SqlServerTableParameters.BuildTagRecords([row]),
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

    public void BindRegisterDefinitions(
        DbCommand command,
        int namespaceId,
        DateTime manifestGenerationUtc,
        IReadOnlyList<DefinitionValues> rows,
        string schema
    )
    {
        var sql = (SqlCommand)command;
        sql.Parameters.Add(new SqlParameter("@p_namespace_id", SqlDbType.Int) { Value = namespaceId });
        sql.Parameters.Add(new SqlParameter("@p_manifest_generation", SqlDbType.DateTime2) { Value = manifestGenerationUtc });
        sql.Parameters.Add(
            new SqlParameter
            {
                ParameterName = "@p_definitions",
                SqlDbType = SqlDbType.Structured,
                TypeName = $"{schema}.job_definition_batch",
                Value = SqlServerTableParameters.BuildDefinitionRecords(rows),
            }
        );
    }

    public void BindRegisterSchedules(
        DbCommand command,
        IReadOnlyList<DefinitionSchedules> definitions,
        IReadOnlyList<Guid> slotRefs,
        string schema
    )
    {
        var sql = (SqlCommand)command;
        sql.Parameters.Add(new SqlParameter("@p_namespace_id", SqlDbType.Int) { Value = definitions[0].NamespaceId });
        sql.Parameters.Add(
            new SqlParameter
            {
                ParameterName = "@p_definitions",
                SqlDbType = SqlDbType.Structured,
                TypeName = $"{schema}.job_schedule_slot_batch",
                Value = SqlServerTableParameters.BuildSlotRecords(definitions, slotRefs),
            }
        );
        sql.Parameters.Add(
            new SqlParameter
            {
                ParameterName = "@p_schedules",
                SqlDbType = SqlDbType.Structured,
                TypeName = $"{schema}.job_schedule_upsert_batch",
                Value = SqlServerTableParameters.BuildScheduleUpsertRecords(definitions),
            }
        );
    }

    public void BindRecurringCompletion(DbCommand command, CompleteExecutionCommand request, string schema)
    {
        var sql = (SqlCommand)command;
        var resultBytes = request.Result.IsEmpty ? [] : request.Result.ToArray();

        AddParameter(sql, "@p_id", SqlDbType.BigInt, request.JobId);
        AddParameter(sql, "@p_leased_by_worker_id", SqlDbType.Int, request.WorkerId);
        AddParameter(sql, "@p_execution_number", SqlDbType.Int, request.ExpectedExecutionNumber);
        AddParameter(sql, "@p_reason_code", SqlDbType.TinyInt, request.EventReasonCode is { } reason ? (byte)reason : DBNull.Value);
        AddParameter(sql, "@p_reason_message", SqlDbType.NVarChar, (object?)request.ReasonMessage ?? DBNull.Value, size: 512);
        AddParameter(sql, "@p_result_format_id", SqlDbType.TinyInt, request.ResultFormatId);
        AddParameter(sql, "@p_result", SqlDbType.VarBinary, resultBytes);
        AddParameter(sql, "@p_execution_succeeded", SqlDbType.Bit, request.Outcome == CompletionKind.Succeeded);
        AddParameter(sql, "@p_duration_ms", SqlDbType.Int, request.DurationMs is { } duration ? duration : DBNull.Value);
        // A re-arm status marks a fire that ran nothing; the slot's next run still comes from the schedules.
        AddParameter(sql, "@p_reschedule_status_code", SqlDbType.TinyInt, request.RescheduleStatusCode is { } rearm ? rearm : DBNull.Value);
        AddParameter(sql, "@p_reschedule_delay_seconds", SqlDbType.Int, DBNull.Value);
        AddParameter(
            sql,
            "@p_reschedule_resume_at_utc",
            SqlDbType.DateTime2,
            request.RescheduleResumeAtUtc is { } resumeAt ? resumeAt : DBNull.Value
        );
        AddParameter(sql, "@p_wait_signal_name", SqlDbType.NVarChar, DBNull.Value, size: 128);
        AddParameter(sql, "@p_handler_status_code", SqlDbType.TinyInt, DBNull.Value);
        AddParameter(sql, "@p_retention_seconds", SqlDbType.Int, request.RetentionSeconds is { } retention ? retention : DBNull.Value);
        AddParameter(sql, "@p_final_status", SqlDbType.TinyInt, (byte)request.FinalStatus!.Value);
        AddParameter(sql, "@p_job_next_run_at_utc", SqlDbType.DateTime2, request.JobNextRunAtUtc is { } nextRun ? nextRun : DBNull.Value);
        AddParameter(sql, "@p_failure_count", SqlDbType.Int, request.FailureCount is { } failureCount ? failureCount : DBNull.Value);
        AddParameter(sql, "@p_recurring_result_cap", SqlDbType.Int, request.RecurringResultCap);
        AddParameter(
            sql,
            "@p_schedule_advances",
            SqlDbType.Structured,
            SqlServerTableParameters.BuildScheduleAdvanceRecords(request.ScheduleAdvances)!,
            typeName: $"{schema}.job_schedule_advance_batch"
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
}
