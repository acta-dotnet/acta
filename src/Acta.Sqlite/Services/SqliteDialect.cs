using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using Acta.Configuration;
using Acta.Features.Definitions;
using Acta.Features.Execution;
using Acta.Features.Jobs;
using Acta.Features.Schedules;
using Acta.Relational.Commands;
using Acta.Relational.Connections;
using Acta.Relational.Schema;
using Microsoft.Data.Sqlite;
using static Acta.Sqlite.Features.Shared.SqliteCommandParameters;

namespace Acta.Sqlite.Services;

/// <summary>
/// SQLite <see cref="ISqlDialect"/>: connection creation, generic parameter coercion, and inline-SQL
/// execution behavior. Feature stores own their command shapes and bind provider parameters directly.
/// </summary>
internal sealed class SqliteDialect : ISqlDialect
{
    // Instants are stored as epoch milliseconds (INTEGER) so numeric comparison matches chronological
    // order on the hot path. Every bound instant goes through ToUnixMs; every db_now reading uses the
    // {{now}} token (CAST(unixepoch('now','subsec')*1000 AS INTEGER)).
    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // Per-connection PRAGMAs, set on every open. journal_mode (WAL) persists in the file header and is
    // set once by the migrator; synchronous is per-connection and chosen by the execution profile.
    private readonly string _connectionPragmas;

    public SqliteDialect(ExecutionProfile profile)
    {
        var synchronous = profile == ExecutionProfile.Direct ? "NORMAL" : "FULL";
        _connectionPragmas = $"PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000; PRAGMA synchronous = {synchronous};";
    }

    public DbProvider Provider => DbProvider.Sqlite;

    public string DialectToken => "sqlite";

    public bool SupportsRoutines => false;

    public bool ResultSetIsLast => true;

    public bool WrapsMutationInTransaction => true;

    // SQLITE_BUSY (5) / SQLITE_LOCKED (6): retry the rolled-back store operation.
    public bool IsTransientConflict(Exception exception) => exception is SqliteException { SqliteErrorCode: 5 or 6 };

    // BEGIN IMMEDIATE takes the reserved write lock up front so concurrent writers honor busy_timeout.
    public DbTransaction BeginImmediateTransaction(DbConnection connection) =>
        ((SqliteConnection)connection).BeginTransaction(deferred: false);

    public DbConnection CreateConnection(string connectionString)
    {
        var connection = new SqliteConnection(connectionString);
        connection.StateChange += OnStateChange;
        return connection;
    }

    public bool OwnsConnection(DbConnection connection) => connection is SqliteConnection;

    // Caller connections we have already prepared, so repeat transactional enqueues on one long-lived
    // connection skip re-registering the functions and the foreign_keys PRAGMA round trip. Keyed weakly so
    // a collected caller connection drops out; Microsoft.Data.Sqlite re-applies CreateFunction on reopen,
    // so a prepared connection stays valid across the caller's own close/reopen.
    private static readonly ConditionalWeakTable<DbConnection, object> PreparedCallerConnections = new();

    // Caller-transaction preparation: the caller made this SqliteConnection itself, so our StateChange
    // hook never ran. Install only the two connection-local functions the inline enqueue SQL needs and
    // verify foreign_keys is ON; never touch the busy timeout, synchronous mode, or transaction kind.
    public void PrepareCallerConnection(DbConnection connection)
    {
        if (PreparedCallerConnections.TryGetValue(connection, out _))
        {
            return;
        }

        var sqlite = (SqliteConnection)connection;
        InstallFunctions(sqlite);

        using var pragma = sqlite.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys;";
        var enabled = Convert.ToInt64(pragma.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
        if (!enabled)
        {
            throw new InvalidOperationException(
                "The caller-owned SQLite connection has foreign_keys disabled; Acta's enqueue relies on foreign-key enforcement. "
                    + "Open the connection with 'PRAGMA foreign_keys = ON' before starting the transaction."
            );
        }

        PreparedCallerConnections.AddOrUpdate(connection, connection);
    }

    private void OnStateChange(object? sender, StateChangeEventArgs e)
    {
        if (e.CurrentState != ConnectionState.Open || sender is not SqliteConnection connection)
        {
            return;
        }

        InstallFunctions(connection);

        using var pragma = connection.CreateCommand();
        pragma.CommandText = _connectionPragmas;
        pragma.ExecuteNonQuery();
    }

    private static void InstallFunctions(SqliteConnection connection)
    {
        connection.CreateFunction<string?, byte[]?>(
            "acta_blob",
            static text => text is null ? null : Convert.FromBase64String(text),
            isDeterministic: true
        );

        // SQLite has no RAISE() outside triggers; provider store SQL uses this function for domain rejects.
        connection.CreateFunction<string, long>(
            "acta_error",
            static message => throw new SqliteException(message, 1),
            isDeterministic: true
        );
    }

    public DbParameter CreateParameter(DbParameterSpec spec)
    {
        DbParams.Validate(spec);
        return new SqliteParameter { ParameterName = "@" + spec.Name, Value = ToSqliteValue(spec.Kind, DbParams.Coerce(spec)) };
    }

    // SupportsRoutines is false; stores never reach the routine-invocation path.
    public void ConfigureRoutineCommand(DbCommand command, string schema, string routineName) =>
        throw new NotSupportedException("SQLite has no stored routines; store commands run as inline SQL.");

    internal static object ToSqliteValue(DbKind kind, object value)
    {
        if (value is DBNull)
        {
            return DBNull.Value;
        }

        return kind switch
        {
            DbKind.UtcInstant => ToUnixMs((DateTime)value),
            DbKind.Boolean => (bool)value ? 1L : 0L,
            DbKind.Guid => ((Guid)value).ToString(),
            DbKind.Decimal => Convert.ToDouble(value, CultureInfo.InvariantCulture),
            _ => value,
        };
    }

    internal static long ToUnixMs(DateTime value) => (long)(DbParams.ToUtc(value) - UnixEpoch).TotalMilliseconds;

    public void BindEnqueueBatch(DbCommand command, IReadOnlyList<JobEnqueueRow> rows, IReadOnlyList<Guid> jobRefs, string schema)
    {
        var jsonRows = JsonArray(
            rows,
            (writer, row, index) =>
            {
                writer.WriteNumber("ordinal", index);
                writer.WriteString("job_ref", jobRefs[index].ToString());
                writer.WriteString("namespace_name", row.NamespaceName);
                writer.WriteString("job_name", row.JobName);
                WriteStringOrNull(writer, "deduplication_key", row.DeduplicationKey);
                WriteStringOrNull(writer, "correlation_key", row.CorrelationKey);
                WriteNumberOrNull(writer, "priority_override", row.PriorityOverride is { } priority ? (short)priority : (short?)null);
                writer.WriteNumber("input_format_id", row.Input.Format.Id);
                WriteBase64OrNull(writer, "input", row.Input.Format.IsNone ? (ReadOnlyMemory<byte>?)null : row.Input.Data);
                WriteStringOrNull(writer, "exclusive_key", row.ExclusiveKey);
                WriteUtcOrNull(writer, "next_run_at_utc", row.NextRunAtUtc);
                WriteNumberOrNull(writer, "delay_seconds", row.DelaySeconds);
                WriteNumberOrNull(writer, "parent_id", row.ParentId);
                WriteStringOrNull(writer, "tenant_key", row.TenantKey);
            }
        );

        var tagItems = new List<(Guid JobRef, string Name, string? Value, string? ValueSearch)>();
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Tags is not { Count: > 0 } tags)
            {
                continue;
            }

            foreach (var tag in tags)
            {
                tagItems.Add((jobRefs[i], tag.Name, tag.Value, TagValueSearch.Normalize(tag.Value)));
            }
        }

        var jsonTags = JsonArray(
            tagItems,
            (writer, tag, _) =>
            {
                writer.WriteString("job_ref", tag.JobRef.ToString());
                writer.WriteString("name", tag.Name);
                WriteStringOrNull(writer, "value", tag.Value);
                WriteStringOrNull(writer, "value_search", tag.ValueSearch);
            }
        );

        AddText(command, "@p_rows", jsonRows);
        AddText(command, "@p_tags", jsonTags);
    }

    public void BindEnqueueOne(DbCommand command, JobEnqueueRow row, Guid jobRef, string schema)
    {
        AddText(command, "@p_job_ref", jobRef.ToString());
        AddText(command, "@p_namespace_name", row.NamespaceName);
        AddText(command, "@p_job_name", row.JobName);
        AddNullableText(command, "@p_deduplication_key", row.DeduplicationKey);
        AddNullableText(command, "@p_correlation_key", row.CorrelationKey);
        AddNullableInt(command, "@p_priority_override", row.PriorityOverride is { } priority ? (short)priority : null);
        AddInt(command, "@p_input_format_id", row.Input.Format.Id);
        AddNullableBlob(command, "@p_input", row.Input.Format.IsNone ? null : row.Input.Data.ToArray());
        AddNullableText(command, "@p_exclusive_key", row.ExclusiveKey);
        AddNullableInt(command, "@p_next_run_at_utc", row.NextRunAtUtc is { } nextRun ? ToUnixMs(nextRun) : null);
        AddNullableInt(command, "@p_delay_seconds", row.DelaySeconds);
        AddNullableInt(command, "@p_parent_id", row.ParentId);
        AddNullableText(command, "@p_tenant_key", row.TenantKey);

        var jsonTags = JsonArray(
            row.Tags ?? [],
            (writer, tag, _) =>
            {
                writer.WriteString("name", tag.Name);
                WriteStringOrNull(writer, "value", tag.Value);
                WriteStringOrNull(writer, "value_search", TagValueSearch.Normalize(tag.Value));
            }
        );
        AddText(command, "@p_tags", jsonTags);
    }

    public void BindRegisterJobDefinitions(
        DbCommand command,
        short namespaceId,
        DateTime manifestGenerationUtc,
        IReadOnlyList<JobDefinitionRow> rows,
        string schema
    )
    {
        AddInt(command, "@p_namespace_id", namespaceId);
        AddInt(command, "@p_manifest_generation", ToUnixMs(manifestGenerationUtc));

        var definitions = JsonArray(
            rows,
            (writer, row, _) =>
            {
                writer.WriteString("name", row.Name);
                writer.WriteNumber("priority_code", row.PriorityCode);
                writer.WriteNumber("max_attempts", row.MaxAttempts);
                writer.WriteString("backoff", row.Backoff);
                writer.WriteNumber("execution_timeout_seconds", row.ExecutionTimeoutSeconds);
                writer.WriteNumber("deadline_seconds", row.DeadlineSeconds);
                writer.WriteNumber("deadline_behavior_code", row.DeadlineBehaviorCode);
                writer.WriteNumber("retention_seconds", row.JobRetentionSeconds);
                writer.WriteString("input_type_name", row.InputTypeName);
                WriteStringOrNull(writer, "output_type_name", row.OutputTypeName);
                writer.WriteNumber("input_format_id", row.InputFormatId);
                writer.WriteString("input_format_name", row.InputFormatName);
                writer.WriteNumber("output_format_id", row.OutputFormatId);
                writer.WriteString("output_format_name", row.OutputFormatName);
                writer.WriteNumber("audit_level_code", row.AuditLevelCode);
                writer.WriteNumber("alert_profile_code", row.AlertProfileCode);
                WriteStringOrNull(writer, "alert_channel_name", row.AlertChannelName);
                WriteStringOrNull(writer, "runbook_url", row.RunbookUrl);
                WriteStringOrNull(writer, "display_name", row.DisplayName);
                WriteStringOrNull(writer, "description", row.Description);
                writer.WriteString("definition_hash", row.DefinitionHash);
            }
        );

        AddText(command, "@p_definitions", definitions);
    }

    public void BindRegisterScheduledJobs(
        DbCommand command,
        IReadOnlyList<DefinitionSchedules> definitions,
        IReadOnlyList<Guid> slotRefs,
        string schema
    )
    {
        AddInt(command, "@p_namespace_id", definitions[0].NamespaceId);

        var definitionRows = JsonArray(
            definitions,
            (writer, definition, index) =>
            {
                writer.WriteNumber("definition_id", definition.DefinitionId);
                writer.WriteString("job_ref", slotRefs[index].ToString());
                writer.WriteString("deduplication_key", definition.JobName);
                writer.WriteNumber("input_format_id", definition.InputFormatId);
                WriteBase64OrNull(writer, "input", definition.Input.IsEmpty ? (ReadOnlyMemory<byte>?)null : definition.Input);
                writer.WriteNumber("audit_level_code", (short)definition.AuditLevel);
                writer.WriteNumber("slot_status_code", (short)definition.SlotStatus);
                WriteUtcOrNull(writer, "slot_next_run_at_utc", definition.SlotMinNextRunAtUtc);
            }
        );

        var scheduleItems = new List<(int DefinitionId, SlotSchedule Schedule)>();
        foreach (var definition in definitions)
        {
            foreach (var schedule in definition.Schedules)
            {
                scheduleItems.Add((definition.DefinitionId, schedule));
            }
        }

        var scheduleRows = JsonArray(
            scheduleItems,
            (writer, item, _) =>
            {
                writer.WriteNumber("definition_id", item.DefinitionId);
                writer.WriteString("name", item.Schedule.Name);
                writer.WriteString("expression", item.Schedule.Expression);
                writer.WriteString("time_zone_id", string.IsNullOrWhiteSpace(item.Schedule.TimeZone) ? "UTC" : item.Schedule.TimeZone);
                writer.WriteNumber("expression_kind_code", (short)item.Schedule.ExpressionKind);
                writer.WriteNumber("misfire_strategy_code", (short)item.Schedule.Misfire);
                WriteUtcOrNull(writer, "next_run_at_utc", item.Schedule.NextRunAtUtc);
                WriteStringOrNull(writer, "description", item.Schedule.Description);
            }
        );

        AddText(command, "@p_definitions", definitionRows);
        AddText(command, "@p_schedules", scheduleRows);
    }

    public void BindRecurringCompletion(DbCommand command, CompleteExecutionRequest request, string schema)
    {
        // results.result is NOT NULL; preserve an empty non-none result as a zero-length blob.
        var resultBytes = request.Result.IsEmpty ? [] : request.Result.ToArray();

        AddInt(command, "@p_id", request.JobId);
        AddInt(command, "@p_leased_by_worker_id", request.WorkerId);
        AddInt(command, "@p_execution_number", request.ExpectedExecutionNumber);
        AddNullableInt(command, "@p_reason_code", request.JobEventReasonCode is { } reason ? (short)reason : null);
        AddNullableText(command, "@p_reason_message", request.ReasonMessage);
        AddInt(command, "@p_result_format_id", request.ResultFormatId);
        AddNullableBlob(command, "@p_result", resultBytes);
        AddInt(command, "@p_execution_succeeded", request.Outcome == ExecutionOutcome.Succeeded ? 1 : 0);
        AddNullableInt(command, "@p_duration_ms", request.DurationMs);
        // Re-arm / signal / handler-status scalars are inert on the recurring path; bind NULL so the
        // inline body's named-parameter set is always complete.
        AddNullableInt(command, "@p_reschedule_status_code", null);
        AddNullableInt(command, "@p_reschedule_delay_seconds", null);
        AddNullableText(command, "@p_reschedule_resume_at_utc", null);
        AddNullableText(command, "@p_wait_signal_name", null);
        AddNullableInt(command, "@p_handler_status_code", null);
        AddNullableInt(command, "@p_retention_seconds", request.RetentionSeconds);
        AddInt(command, "@p_final_status", (short)request.FinalStatus!.Value);
        AddNullableInt(command, "@p_job_next_run_at_utc", request.JobNextRunAtUtc is { } nextRun ? ToUnixMs(nextRun) : null);
        AddNullableInt(command, "@p_failure_count", request.FailureCount);
        AddInt(command, "@p_recurring_result_cap", request.RecurringResultCap);

        var advances = request.ScheduleAdvances ?? (IReadOnlyList<ScheduleAdvance>)[];
        var json = JsonArray(
            advances,
            (writer, advance, _) =>
            {
                writer.WriteNumber("schedule_id", advance.ScheduleId);
                WriteUtcOrNull(writer, "next_run_at_utc", advance.NextRunAtUtc);
            }
        );
        AddText(command, "@p_schedule_advances", json);
    }

    // SQLite has no batched-completion routine; the runtime degrades Bulk to Direct before the store
    // is reached, so this bind is never invoked.
    public void BindCompleteExecutionsBatch(DbCommand command, IReadOnlyList<CompleteExecutionRequest> requests, string schema) =>
        throw new NotSupportedException("The SQLite provider has no batched-completion routine; Bulk degrades to Direct.");
}
