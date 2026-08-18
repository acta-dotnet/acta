using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Acta.Relational.Commands;
using Acta.Relational.Schema;
using Acta.Runtime.Kernel;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Definitions;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Runtime.Modules.Execution.Schedules;
using Npgsql;
using NpgsqlTypes;

namespace Acta.Postgres.Services;

/// <summary>
/// PostgreSQL <see cref="ISqlDialect"/>: connection creation, generic parameter coercion, and
/// routine invocation. Feature stores own their command shapes and bind provider parameters directly.
/// </summary>
internal sealed class PostgresDialect : ISqlDialect
{
    public DbProvider Provider => DbProvider.Postgres;

    public string DialectToken => "pg";

    public bool SupportsRoutines => true;

    public bool IsTransientConflict(Exception exception) =>
        exception is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected };

    public DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

    public bool OwnsConnection(DbConnection connection) => connection is NpgsqlConnection;

    public DbParameter CreateParameter(DbParameterSpec spec)
    {
        DbParams.Validate(spec);
        var coercedValue = DbParams.Coerce(spec);
        var parameter = new NpgsqlParameter { ParameterName = "@" + spec.Name, NpgsqlDbType = MapKind(spec) };

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

        parameter.Value = coercedValue is byte value ? (object)(short)value : coercedValue;
        return parameter;
    }

    public void ConfigureRoutineCommand(DbCommand command, string schema, string routineName)
    {
        IdentifierSyntax.ValidateBareIdentifier(routineName, nameof(routineName));

        // PostgreSQL functions returning TABLE are invoked via SELECT *. Store parameters are emitted
        // in insertion order because function arguments are positional; parameter names still select
        // each NpgsqlParameter value during substitution.
        var text = new StringBuilder("SELECT * FROM ");
        text.Append(schema);
        text.Append('.');
        text.Append(routineName);
        text.Append('(');
        for (var i = 0; i < command.Parameters.Count; i++)
        {
            if (i > 0)
            {
                text.Append(", ");
            }

            var parameterName = command.Parameters[i].ParameterName.AsSpan().TrimStart('@');
            text.Append('@');
            text.Append(parameterName);
        }
        text.Append(')');

        command.CommandText = text.ToString();
        command.CommandType = CommandType.Text;
    }

    private static NpgsqlDbType MapKind(DbParameterSpec parameter) =>
        parameter.Kind switch
        {
            DbKind.Boolean => NpgsqlDbType.Boolean,
            DbKind.Byte => NpgsqlDbType.Smallint,
            DbKind.Int16 => NpgsqlDbType.Smallint,
            DbKind.Int32 => NpgsqlDbType.Integer,
            DbKind.Int64 => NpgsqlDbType.Bigint,
            DbKind.Guid => NpgsqlDbType.Uuid,
            DbKind.UtcInstant => NpgsqlDbType.TimestampTz,
            DbKind.Decimal => NpgsqlDbType.Numeric,
            DbKind.AsciiString => NpgsqlDbType.Varchar,
            DbKind.UnicodeString => NpgsqlDbType.Varchar,
            DbKind.Bytes => NpgsqlDbType.Bytea,
            DbKind.BinaryPayload => NpgsqlDbType.Bytea,
            _ => throw new InvalidOperationException($"Unmapped DbKind '{parameter.Kind}' for Postgres parameter '{parameter.Name}'."),
        };

    public void BindEnqueueBatch(DbCommand command, IReadOnlyList<JobEnqueueRow> rows, IReadOnlyList<Guid> jobRefs, string schema)
    {
        var postgres = (NpgsqlCommand)command;
        var count = rows.Count;
        var ordinals = new int[count];
        var jobRefValues = new Guid[count];
        var namespaceNames = new string[count];
        var jobNames = new string[count];
        var deduplicationKeys = new string?[count];
        var correlationKeys = new string?[count];
        var priorityOverrides = new short?[count];
        var inputFormatIds = new short[count];
        var inputs = new byte[]?[count];
        var exclusiveKeys = new string?[count];
        var nextRunAtUtcs = new DateTime?[count];
        var delaySeconds = new int?[count];
        var parentIds = new long?[count];
        var tenantKeys = new string?[count];
        var tenantOverrides = new bool[count];

        for (var i = 0; i < count; i++)
        {
            var row = rows[i];
            ordinals[i] = i;
            jobRefValues[i] = jobRefs[i];
            namespaceNames[i] = row.NamespaceName;
            jobNames[i] = row.JobName;
            deduplicationKeys[i] = row.DeduplicationKey;
            correlationKeys[i] = row.CorrelationKey;
            priorityOverrides[i] = row.PriorityOverride is { } priority ? (short)priority : null;
            inputFormatIds[i] = row.Input.Format.Id;
            inputs[i] = row.Input.Format.IsNone ? null : row.Input.Data.ToArray();
            exclusiveKeys[i] = row.ExclusiveKey;
            nextRunAtUtcs[i] = row.NextRunAtUtc is { } nextRun ? DbParams.ToUtc(nextRun) : null;
            delaySeconds[i] = row.DelaySeconds;
            parentIds[i] = row.ParentId;
            tenantKeys[i] = row.TenantKey;
            tenantOverrides[i] = row.OverrideParentTenant;
        }

        AddArray(postgres, "@p_b_ordinal", NpgsqlDbType.Integer, ordinals);
        AddArray(postgres, "@p_b_job_ref", NpgsqlDbType.Uuid, jobRefValues);
        AddArray(postgres, "@p_b_namespace_name", NpgsqlDbType.Varchar, namespaceNames);
        AddArray(postgres, "@p_b_job_name", NpgsqlDbType.Varchar, jobNames);
        AddArray(postgres, "@p_b_deduplication_key", NpgsqlDbType.Varchar, deduplicationKeys);
        AddArray(postgres, "@p_b_correlation_key", NpgsqlDbType.Varchar, correlationKeys);
        AddArray(postgres, "@p_b_priority_override", NpgsqlDbType.Smallint, priorityOverrides);
        AddArray(postgres, "@p_b_input_format_id", NpgsqlDbType.Smallint, inputFormatIds);
        AddArray(postgres, "@p_b_input", NpgsqlDbType.Bytea, inputs);
        AddArray(postgres, "@p_b_exclusive_key", NpgsqlDbType.Varchar, exclusiveKeys);
        AddArray(postgres, "@p_b_next_run_at_utc", NpgsqlDbType.TimestampTz, nextRunAtUtcs);
        AddArray(postgres, "@p_b_delay_seconds", NpgsqlDbType.Integer, delaySeconds);
        AddArray(postgres, "@p_b_parent_id", NpgsqlDbType.Bigint, parentIds);
        AddArray(postgres, "@p_b_tenant_key", NpgsqlDbType.Varchar, tenantKeys);
        AddArray(postgres, "@p_b_tenant_override", NpgsqlDbType.Boolean, tenantOverrides);

        var tagCount = rows.Sum(row => row.Tags?.Count ?? 0);
        var tagOrdinals = new int[tagCount];
        var tagNames = new string[tagCount];
        var tagValues = new string?[tagCount];
        var tagValueSearches = new string?[tagCount];
        var tagIndex = 0;
        for (var i = 0; i < count; i++)
        {
            if (rows[i].Tags is not { Count: > 0 } tags)
            {
                continue;
            }

            foreach (var tag in tags)
            {
                tagOrdinals[tagIndex] = i;
                tagNames[tagIndex] = tag.Name;
                tagValues[tagIndex] = tag.Value;
                tagValueSearches[tagIndex] = TagValueSearch.Normalize(tag.Value);
                tagIndex++;
            }
        }

        AddArray(postgres, "@p_t_ordinal", NpgsqlDbType.Integer, tagOrdinals);
        AddArray(postgres, "@p_t_name", NpgsqlDbType.Varchar, tagNames);
        AddArray(postgres, "@p_t_value", NpgsqlDbType.Varchar, tagValues);
        AddArray(postgres, "@p_t_value_search", NpgsqlDbType.Varchar, tagValueSearches);
    }

    [SuppressMessage(
        "Maintainability",
        "CA1508:Avoid dead conditional code",
        Justification = "False positive on both flagged lines. JobEnqueueRow.DelaySeconds is int? and ParentId is "
            + "long?; boxing a nullable value type whose HasValue is false yields a null reference, so the cast is "
            + "null exactly when the column must be NULL. CA1508 models that boxing conversion as never-null "
            + "and is wrong here: deleting the branch it calls dead would bind a CLR null rather than "
            + "DBNull.Value, which is not the same thing to any provider. The nullable reference-typed columns "
            + "bound beside these use the identical idiom and are not flagged. "
            + "ParentId is null for every root job, so that branch runs on essentially every enqueue. Kept "
            + "identical to the SQL Server binder."
    )]
    public void BindEnqueueOne(DbCommand command, JobEnqueueRow row, Guid jobRef, string schema)
    {
        var postgres = (NpgsqlCommand)command;
        AddScalar(postgres, "@p_job_ref", NpgsqlDbType.Uuid, jobRef);
        AddScalar(postgres, "@p_namespace_name", NpgsqlDbType.Varchar, row.NamespaceName);
        AddScalar(postgres, "@p_job_name", NpgsqlDbType.Varchar, row.JobName);
        AddScalar(postgres, "@p_deduplication_key", NpgsqlDbType.Varchar, (object?)row.DeduplicationKey ?? DBNull.Value);
        AddScalar(postgres, "@p_correlation_key", NpgsqlDbType.Varchar, (object?)row.CorrelationKey ?? DBNull.Value);
        AddScalar(
            postgres,
            "@p_priority_override",
            NpgsqlDbType.Smallint,
            row.PriorityOverride is { } priority ? (short)priority : DBNull.Value
        );
        AddScalar(postgres, "@p_input_format_id", NpgsqlDbType.Smallint, (short)row.Input.Format.Id);
        AddScalar(postgres, "@p_input", NpgsqlDbType.Bytea, row.Input.Format.IsNone ? DBNull.Value : row.Input.Data.ToArray());
        AddScalar(postgres, "@p_exclusive_key", NpgsqlDbType.Varchar, (object?)row.ExclusiveKey ?? DBNull.Value);
        AddScalar(
            postgres,
            "@p_next_run_at_utc",
            NpgsqlDbType.TimestampTz,
            row.NextRunAtUtc is { } nextRun ? DbParams.ToUtc(nextRun) : DBNull.Value
        );
        AddScalar(postgres, "@p_delay_seconds", NpgsqlDbType.Integer, (object?)row.DelaySeconds ?? DBNull.Value);
        AddScalar(postgres, "@p_parent_id", NpgsqlDbType.Bigint, (object?)row.ParentId ?? DBNull.Value);
        AddScalar(postgres, "@p_tenant_key", NpgsqlDbType.Varchar, (object?)row.TenantKey ?? DBNull.Value);
        AddScalar(postgres, "@p_tenant_override", NpgsqlDbType.Boolean, row.OverrideParentTenant);

        var tagCount = row.Tags?.Count ?? 0;
        var tagNames = new string[tagCount];
        var tagValues = new string?[tagCount];
        var tagValueSearches = new string?[tagCount];
        for (var i = 0; i < tagCount; i++)
        {
            tagNames[i] = row.Tags![i].Name;
            tagValues[i] = row.Tags[i].Value;
            tagValueSearches[i] = TagValueSearch.Normalize(row.Tags[i].Value);
        }

        AddArray(postgres, "@p_t_name", NpgsqlDbType.Varchar, tagNames);
        AddArray(postgres, "@p_t_value", NpgsqlDbType.Varchar, tagValues);
        AddArray(postgres, "@p_t_value_search", NpgsqlDbType.Varchar, tagValueSearches);
    }

    public void BindRegisterJobDefinitions(
        DbCommand command,
        short namespaceId,
        DateTime manifestGenerationUtc,
        IReadOnlyList<JobDefinitionRow> rows,
        string schema
    )
    {
        var postgres = (NpgsqlCommand)command;
        var count = rows.Count;
        var names = new string[count];
        var priorityCodes = new short[count];
        var maxAttempts = new short[count];
        var backoff = new string[count];
        var executionTimeout = new int[count];
        var deadlineSeconds = new int[count];
        var deadlineBehavior = new short[count];
        var jobRetention = new int[count];
        var inputTypeNames = new string[count];
        var outputTypeNames = new string?[count];
        var inputFormatIds = new short[count];
        var inputFormatNames = new string[count];
        var outputFormatIds = new short[count];
        var outputFormatNames = new string[count];
        var auditLevelCodes = new short[count];
        var alertProfileCodes = new short[count];
        var tenantRequirementCodes = new short[count];
        var alertChannelNames = new string?[count];
        var runbookUrls = new string?[count];
        var displayNames = new string?[count];
        var descriptions = new string?[count];
        var definitionHashes = new string[count];

        for (var i = 0; i < count; i++)
        {
            var row = rows[i];
            names[i] = row.Name;
            priorityCodes[i] = row.PriorityCode;
            maxAttempts[i] = row.MaxAttempts;
            backoff[i] = row.Backoff;
            executionTimeout[i] = row.ExecutionTimeoutSeconds;
            deadlineSeconds[i] = row.DeadlineSeconds;
            deadlineBehavior[i] = row.DeadlineBehaviorCode;
            jobRetention[i] = row.JobRetentionSeconds;
            inputTypeNames[i] = row.InputTypeName;
            outputTypeNames[i] = row.OutputTypeName;
            inputFormatIds[i] = row.InputFormatId;
            inputFormatNames[i] = row.InputFormatName;
            outputFormatIds[i] = row.OutputFormatId;
            outputFormatNames[i] = row.OutputFormatName;
            auditLevelCodes[i] = row.AuditLevelCode;
            alertProfileCodes[i] = row.AlertProfileCode;
            tenantRequirementCodes[i] = row.TenantRequirementCode;
            alertChannelNames[i] = row.AlertChannelName;
            runbookUrls[i] = row.RunbookUrl;
            displayNames[i] = row.DisplayName;
            descriptions[i] = row.Description;
            definitionHashes[i] = row.DefinitionHash;
        }

        // PostgreSQL routine arguments are positional; this order matches register_job_definitions.
        AddScalar(postgres, "@p_namespace_id", NpgsqlDbType.Smallint, namespaceId);
        AddScalar(postgres, "@p_manifest_generation", NpgsqlDbType.TimestampTz, manifestGenerationUtc);
        AddArray(postgres, "@p_d_name", NpgsqlDbType.Varchar, names);
        AddArray(postgres, "@p_d_priority_code", NpgsqlDbType.Smallint, priorityCodes);
        AddArray(postgres, "@p_d_max_attempts", NpgsqlDbType.Smallint, maxAttempts);
        AddArray(postgres, "@p_d_backoff", NpgsqlDbType.Varchar, backoff);
        AddArray(postgres, "@p_d_execution_timeout", NpgsqlDbType.Integer, executionTimeout);
        AddArray(postgres, "@p_d_deadline_seconds", NpgsqlDbType.Integer, deadlineSeconds);
        AddArray(postgres, "@p_d_deadline_behavior", NpgsqlDbType.Smallint, deadlineBehavior);
        AddArray(postgres, "@p_d_job_retention", NpgsqlDbType.Integer, jobRetention);
        AddArray(postgres, "@p_d_input_type_name", NpgsqlDbType.Varchar, inputTypeNames);
        AddArray(postgres, "@p_d_output_type_name", NpgsqlDbType.Varchar, outputTypeNames);
        AddArray(postgres, "@p_d_input_format_id", NpgsqlDbType.Smallint, inputFormatIds);
        AddArray(postgres, "@p_d_input_format_name", NpgsqlDbType.Varchar, inputFormatNames);
        AddArray(postgres, "@p_d_output_format_id", NpgsqlDbType.Smallint, outputFormatIds);
        AddArray(postgres, "@p_d_output_format_name", NpgsqlDbType.Varchar, outputFormatNames);
        AddArray(postgres, "@p_d_audit_level_code", NpgsqlDbType.Smallint, auditLevelCodes);
        AddArray(postgres, "@p_d_alert_profile_code", NpgsqlDbType.Smallint, alertProfileCodes);
        AddArray(postgres, "@p_d_tenant_requirement", NpgsqlDbType.Smallint, tenantRequirementCodes);
        AddArray(postgres, "@p_d_alert_channel_name", NpgsqlDbType.Varchar, alertChannelNames);
        AddArray(postgres, "@p_d_runbook_url", NpgsqlDbType.Varchar, runbookUrls);
        AddArray(postgres, "@p_d_display_name", NpgsqlDbType.Varchar, displayNames);
        AddArray(postgres, "@p_d_description", NpgsqlDbType.Varchar, descriptions);
        AddArray(postgres, "@p_d_definition_hash", NpgsqlDbType.Varchar, definitionHashes);
    }

    public void BindRegisterScheduledJobs(
        DbCommand command,
        IReadOnlyList<DefinitionSchedules> definitions,
        IReadOnlyList<Guid> slotRefs,
        string schema
    )
    {
        var postgres = (NpgsqlCommand)command;
        var definitionCount = definitions.Count;
        var jobRefs = new Guid[definitionCount];
        var definitionIds = new int[definitionCount];
        var deduplicationKeys = new string[definitionCount];
        var inputFormatIds = new short[definitionCount];
        byte[]?[] inputs = new byte[definitionCount][];
        var auditLevels = new short[definitionCount];
        var slotStatuses = new short[definitionCount];
        var slotNextRuns = new DateTime?[definitionCount];
        var scheduleCount = 0;
        for (var i = 0; i < definitionCount; i++)
        {
            var definition = definitions[i];
            jobRefs[i] = slotRefs[i];
            definitionIds[i] = definition.DefinitionId;
            deduplicationKeys[i] = definition.JobName;
            inputFormatIds[i] = definition.InputFormatId;
            inputs[i] = definition.Input.IsEmpty ? null : definition.Input.ToArray();
            auditLevels[i] = (short)definition.AuditLevel;
            slotStatuses[i] = (short)definition.SlotStatus;
            slotNextRuns[i] = definition.SlotMinNextRunAtUtc;
            scheduleCount += definition.Schedules.Count;
        }

        var scheduleDefinitionIds = new int[scheduleCount];
        var scheduleNames = new string[scheduleCount];
        var expressions = new string[scheduleCount];
        var timeZones = new string[scheduleCount];
        var expressionKinds = new short[scheduleCount];
        var misfires = new short[scheduleCount];
        var nextRuns = new DateTime?[scheduleCount];
        var descriptions = new string?[scheduleCount];
        var scheduleIndex = 0;
        foreach (var definition in definitions)
        {
            foreach (var schedule in definition.Schedules)
            {
                scheduleDefinitionIds[scheduleIndex] = definition.DefinitionId;
                scheduleNames[scheduleIndex] = schedule.Name;
                expressions[scheduleIndex] = schedule.Expression;
                timeZones[scheduleIndex] = string.IsNullOrWhiteSpace(schedule.TimeZoneId) ? "UTC" : schedule.TimeZoneId;
                expressionKinds[scheduleIndex] = (short)schedule.ExpressionKind;
                misfires[scheduleIndex] = (short)schedule.MisfireStrategy;
                nextRuns[scheduleIndex] = schedule.NextRunAtUtc;
                descriptions[scheduleIndex] = schedule.Description;
                scheduleIndex++;
            }
        }

        // PostgreSQL routine arguments are positional; this order matches register_scheduled_jobs.
        AddScalar(postgres, "@p_namespace_id", NpgsqlDbType.Smallint, definitions[0].NamespaceId);
        AddArray(postgres, "@p_d_job_ref", NpgsqlDbType.Uuid, jobRefs);
        AddArray(postgres, "@p_d_definition_id", NpgsqlDbType.Integer, definitionIds);
        AddArray(postgres, "@p_d_deduplication_key", NpgsqlDbType.Varchar, deduplicationKeys);
        AddArray(postgres, "@p_d_input_format_id", NpgsqlDbType.Smallint, inputFormatIds);
        AddArray(postgres, "@p_d_input", NpgsqlDbType.Bytea, inputs);
        AddArray(postgres, "@p_d_audit_level", NpgsqlDbType.Smallint, auditLevels);
        AddArray(postgres, "@p_d_slot_status", NpgsqlDbType.Smallint, slotStatuses);
        AddArray(postgres, "@p_d_slot_next_run_at_utc", NpgsqlDbType.TimestampTz, slotNextRuns);
        AddArray(postgres, "@p_s_definition_id", NpgsqlDbType.Integer, scheduleDefinitionIds);
        AddArray(postgres, "@p_s_name", NpgsqlDbType.Varchar, scheduleNames);
        AddArray(postgres, "@p_s_expression", NpgsqlDbType.Varchar, expressions);
        AddArray(postgres, "@p_s_time_zone", NpgsqlDbType.Varchar, timeZones);
        AddArray(postgres, "@p_s_expression_kind", NpgsqlDbType.Smallint, expressionKinds);
        AddArray(postgres, "@p_s_misfire", NpgsqlDbType.Smallint, misfires);
        AddArray(postgres, "@p_s_next_run_at_utc", NpgsqlDbType.TimestampTz, nextRuns);
        AddArray(postgres, "@p_s_description", NpgsqlDbType.Varchar, descriptions);
    }

    public void BindRecurringCompletion(DbCommand command, CompleteExecutionRequest request, string schema)
    {
        var postgres = (NpgsqlCommand)command;
        var resultBytes = request.Result.IsEmpty ? [] : request.Result.ToArray();

        // PostgreSQL routine arguments are positional; this order matches complete_execution.
        AddScalar(postgres, "@p_id", NpgsqlDbType.Bigint, request.JobId);
        AddScalar(postgres, "@p_leased_by_worker_id", NpgsqlDbType.Integer, request.WorkerId);
        AddScalar(postgres, "@p_execution_number", NpgsqlDbType.Integer, request.ExpectedExecutionNumber);
        AddScalar(
            postgres,
            "@p_reason_code",
            NpgsqlDbType.Smallint,
            request.JobEventReasonCode is { } reason ? (short)reason : DBNull.Value
        );
        AddScalar(postgres, "@p_reason_message", NpgsqlDbType.Varchar, (object?)request.ReasonMessage ?? DBNull.Value);
        AddScalar(postgres, "@p_result_format_id", NpgsqlDbType.Smallint, (short)request.ResultFormatId);
        AddScalar(postgres, "@p_result", NpgsqlDbType.Bytea, resultBytes);
        AddScalar(postgres, "@p_execution_succeeded", NpgsqlDbType.Boolean, request.Outcome == ExecutionOutcome.Succeeded);
        AddScalar(postgres, "@p_duration_ms", NpgsqlDbType.Integer, request.DurationMs is { } duration ? duration : DBNull.Value);
        AddScalar(postgres, "@p_reschedule_status_code", NpgsqlDbType.Smallint, DBNull.Value);
        AddScalar(postgres, "@p_reschedule_delay_seconds", NpgsqlDbType.Integer, DBNull.Value);
        AddScalar(postgres, "@p_reschedule_resume_at_utc", NpgsqlDbType.TimestampTz, DBNull.Value);
        AddScalar(postgres, "@p_wait_signal_name", NpgsqlDbType.Varchar, DBNull.Value);
        AddScalar(postgres, "@p_handler_status_code", NpgsqlDbType.Smallint, DBNull.Value);
        AddScalar(
            postgres,
            "@p_retention_seconds",
            NpgsqlDbType.Integer,
            request.RetentionSeconds is { } retention ? retention : DBNull.Value
        );
        AddScalar(postgres, "@p_final_status", NpgsqlDbType.Smallint, (short)request.FinalStatus!.Value);
        AddScalar(
            postgres,
            "@p_job_next_run_at_utc",
            NpgsqlDbType.TimestampTz,
            request.JobNextRunAtUtc is { } nextRun ? nextRun : DBNull.Value
        );
        AddScalar(
            postgres,
            "@p_failure_count",
            NpgsqlDbType.Smallint,
            request.FailureCount is { } failureCount ? failureCount : DBNull.Value
        );
        AddScalar(postgres, "@p_recurring_result_cap", NpgsqlDbType.Integer, request.RecurringResultCap);

        var advances = request.ScheduleAdvances ?? (IReadOnlyList<ScheduleAdvance>)[];
        var scheduleIds = new long[advances.Count];
        var nextRuns = new DateTime?[advances.Count];
        for (var i = 0; i < advances.Count; i++)
        {
            scheduleIds[i] = advances[i].ScheduleId;
            nextRuns[i] = advances[i].NextRunAtUtc;
        }

        AddArray(postgres, "@p_advance_schedule_ids", NpgsqlDbType.Bigint, scheduleIds);
        AddArray(postgres, "@p_advance_next_runs", NpgsqlDbType.TimestampTz, nextRuns);
    }

    public void BindCompleteExecutionsBatch(DbCommand command, IReadOnlyList<CompleteExecutionRequest> requests, string schema)
    {
        var postgres = (NpgsqlCommand)command;
        var count = requests.Count;
        var ordinals = new int[count];
        var ids = new long[count];
        var workerIds = new int[count];
        var executionNumbers = new int[count];
        var succeeded = new bool[count];
        var durationMs = new int?[count];
        var reasonCodes = new short?[count];
        var reasonMessages = new string?[count];
        var resultFormatIds = new short[count];
        var results = new byte[count][];
        var failureCounts = new short?[count];
        var retentionSeconds = new int?[count];

        for (var i = 0; i < count; i++)
        {
            var request = requests[i];
            ordinals[i] = i;
            ids[i] = request.JobId;
            workerIds[i] = request.WorkerId;
            executionNumbers[i] = request.ExpectedExecutionNumber;
            succeeded[i] = request.Outcome == ExecutionOutcome.Succeeded;
            durationMs[i] = request.DurationMs;
            reasonCodes[i] = request.JobEventReasonCode is { } reason ? (short)reason : null;
            reasonMessages[i] = request.ReasonMessage;
            resultFormatIds[i] = request.ResultFormatId;
            results[i] = request.Result.IsEmpty ? [] : request.Result.ToArray();
            failureCounts[i] = request.FailureCount;
            retentionSeconds[i] = request.RetentionSeconds;
        }

        // PostgreSQL routine arguments are positional; this order matches complete_executions_batch.
        AddArray(postgres, "@p_b_ordinal", NpgsqlDbType.Integer, ordinals);
        AddArray(postgres, "@p_b_job_id", NpgsqlDbType.Bigint, ids);
        AddArray(postgres, "@p_b_worker_id", NpgsqlDbType.Integer, workerIds);
        AddArray(postgres, "@p_b_execution_number", NpgsqlDbType.Integer, executionNumbers);
        AddArray(postgres, "@p_b_succeeded", NpgsqlDbType.Boolean, succeeded);
        AddArray(postgres, "@p_b_duration_ms", NpgsqlDbType.Integer, durationMs);
        AddArray(postgres, "@p_b_reason_code", NpgsqlDbType.Smallint, reasonCodes);
        AddArray(postgres, "@p_b_reason_message", NpgsqlDbType.Varchar, reasonMessages);
        AddArray(postgres, "@p_b_result_format_id", NpgsqlDbType.Smallint, resultFormatIds);
        AddArray(postgres, "@p_b_result", NpgsqlDbType.Bytea, results);
        AddArray(postgres, "@p_b_failure_count", NpgsqlDbType.Smallint, failureCounts);
        AddArray(postgres, "@p_b_retention_seconds", NpgsqlDbType.Integer, retentionSeconds);
    }

    /// <summary>Npgsql parameter primitives the dialect's own binders use to shape command parameters.</summary>
    private static void AddScalar(NpgsqlCommand command, string name, NpgsqlDbType type, object value) =>
        command.Parameters.Add(
            new NpgsqlParameter
            {
                ParameterName = name,
                NpgsqlDbType = type,
                Value = value,
            }
        );

    private static void AddArray(NpgsqlCommand command, string name, NpgsqlDbType elementType, object value) =>
        command.Parameters.Add(
            new NpgsqlParameter
            {
                ParameterName = name,
                NpgsqlDbType = NpgsqlDbType.Array | elementType,
                Value = value,
            }
        );
}
