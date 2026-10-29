using System.Buffers;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Acta.Sqlite;

/// <summary>
/// SQLite <see cref="ISqlDialect"/>: connection creation, generic parameter coercion, and inline-SQL
/// execution behavior. Feature stores own their command shapes and bind provider parameters directly.
/// </summary>
internal sealed partial class SqliteDialect : ISqlDialect
{
    /// <summary>
    /// Instants are stored as epoch milliseconds (INTEGER) so numeric comparison matches chronological
    /// order on the hot path. Every bound instant goes through ToUnixMs; every db_now reading uses the
    /// {{now}} token (CAST(unixepoch('now','subsec')*1000 AS INTEGER)).
    /// </summary>
    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Per-connection PRAGMAs, set on every open. journal_mode (WAL) persists in the file header and is
    /// set once by the migrator; synchronous is per-connection, and FULL syncs every commit, so a committed
    /// completion survives power loss under every execution profile.
    /// </summary>
    private const string ConnectionPragmas = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000; PRAGMA synchronous = FULL;";

    public DbProvider Provider => DbProvider.Sqlite;

    public string DialectToken => "sqlite";

    public bool ResultSetIsLast => true;

    /// <summary>
    /// SQLITE_BUSY (5) / SQLITE_LOCKED (6): retry the rolled-back store operation.
    /// </summary>
    public bool IsTransientConflict(Exception exception) => exception is SqliteException { SqliteErrorCode: 5 or 6 };

    /// <summary>
    /// BEGIN IMMEDIATE takes the reserved write lock up front so concurrent writers honor busy_timeout.
    /// </summary>
    public DbTransaction BeginOwnedWriteTransaction(DbConnection connection) =>
        ((SqliteConnection)connection).BeginTransaction(deferred: false);

    public DbConnection CreateConnection(string connectionString)
    {
        var connection = new SqliteConnection(connectionString);
        connection.StateChange += OnStateChange;
        return connection;
    }

    public bool OwnsConnection(DbConnection connection) => connection is SqliteConnection;

    /// <summary>
    /// Caller connections we have already prepared, so repeat transactional enqueues on one long-lived
    /// connection skip re-registering the functions and the foreign_keys PRAGMA round trip. Keyed weakly so
    /// a collected caller connection drops out; Microsoft.Data.Sqlite re-applies CreateFunction on reopen,
    /// so a prepared connection stays valid across the caller's own close/reopen.
    /// </summary>
    private static readonly ConditionalWeakTable<DbConnection, object> PreparedCallerConnections = [];

    /// <summary>
    /// Caller-transaction preparation: the caller made this SqliteConnection itself, so our StateChange
    /// hook never ran. Install only the two connection-local functions the inline enqueue SQL needs and
    /// verify foreign_keys is ON; never touch the busy timeout, synchronous mode, or transaction kind.
    /// </summary>
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
        pragma.CommandText = ConnectionPragmas;
        pragma.ExecuteNonQuery();
    }

    private static void InstallFunctions(SqliteConnection connection)
    {
        // An empty array reaches SQL as NULL, not as a zero-length blob, so SQL that stores a payload with a format and
        // no bytes coalesces the result to X''.
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

    /// <summary>
    /// SQLite embeds inline resources only, so stores never reach routine invocation.
    /// </summary>
    public void ConfigureRoutineCommand(DbCommand command, string schema, string routineName) =>
        throw new NotSupportedException("SQLite has no stored routines; store commands run as inline SQL.");

    internal static object ToSqliteValue(DbKind kind, object value)
    {
        return value is DBNull
            ? DBNull.Value
            : kind switch
            {
                DbKind.UtcInstant => ToUnixMs((DateTime)value),
                DbKind.Boolean => (bool)value ? 1L : 0L,
                DbKind.Guid => ((Guid)value).ToString(),
                _ => value,
            };
    }

    internal static long ToUnixMs(DateTime value) => (long)(DbParams.ToUtc(value) - UnixEpoch).TotalMilliseconds;

    /// <summary>
    /// JSON array text the claim expands with json_each. An empty set binds NULL, the form the claim's
    /// exclusion term short-circuits on.
    /// </summary>
    public void BindExcludedDefinitionIds(DbCommand command, int[]? definitionIds) =>
        AddNullableText(command, "@p_excluded_definition_ids", DbParams.JsonIdArray(definitionIds));

    public void BindHeldLeases(
        DbCommand command,
        IReadOnlyList<(long JobId, int ExecutionNumber)> executions,
        IReadOnlyList<LockToken> locks
    )
    {
        AddText(command, "@p_executions", DbParams.JsonHeldExecutions(executions));
        AddText(command, "@p_locks", DbParams.JsonHeldLocks(locks));
    }

    /// <summary>
    /// The batch as JSON, with each section of the script cut when no row of the batch needs it, because SQLite
    /// prepares every statement of a command on each call: a batch of plain landings runs only the base statements.
    /// </summary>
    public void BindCompleteExecutions(DbCommand command, CompleteExecutionsCommand request, string schema)
    {
        var rows = request.Completions;
        var sql = command.CommandText;
        sql = rows.Any(c => c.WaitSignalName is not null) ? sql : WithoutSection(sql, "waits");
        sql = rows.Any(c => c.RescheduleEventCode is not null || c.HandlerStatusCode is not null || c.FinalStatus is not null)
            ? sql
            : WithoutSection(sql, "shapes");
        sql = rows.Any(c => c.HasParent) ? sql : WithoutSection(sql, "children");
        sql = rows.Any(c => c.HasLane) ? sql : WithoutSection(sql, "lanes");
        sql = rows.Any(c => c.FinalStatus is not null) ? sql : WithoutSection(sql, "recurring");

        command.CommandText = sql;
        AddText(command, "@p_completions", DbParams.JsonCompletions(rows));
    }

    /// <summary>The script without every <c>-- begin name</c> ... <c>-- end name</c> section.</summary>
    internal static string WithoutSection(string sql, string name)
    {
        var begin = $"-- begin {name}";
        var end = $"-- end {name}";
        for (var start = sql.IndexOf(begin, StringComparison.Ordinal); start >= 0; start = sql.IndexOf(begin, StringComparison.Ordinal))
        {
            var stop = sql.IndexOf(end, start, StringComparison.Ordinal);
            if (stop < 0)
            {
                break;
            }
            sql = sql[..start] + sql[(stop + end.Length)..];
        }
        return sql;
    }

    public void BindEnqueueJobs(DbCommand command, IReadOnlyList<JobEnqueueValues> rows, IReadOnlyList<Guid> jobRefs, string schema)
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
                WriteNumberOrNull(
                    writer,
                    "job_priority_code_override",
                    row.PriorityOverride is { } priority ? (short)priority : (short?)null
                );
                writer.WriteNumber("input_format_id", row.Input.Format.Id);
                WriteBase64OrNull(writer, "input", row.Input.Format.IsNone ? (ReadOnlyMemory<byte>?)null : row.Input.Data);
                WriteStringOrNull(writer, "concurrency_key", row.ConcurrencyKey);
                WriteUtcOrNull(writer, "next_run_at_utc", row.NextRunAtUtc);
                WriteNumberOrNull(writer, "delay_seconds", row.DelaySeconds);
                WriteNumberOrNull(writer, "parent_job_id", row.ParentJobId);
                WriteStringOrNull(writer, "tenant_name", row.TenantName);
                writer.WriteNumber("tenant_override", row.OverrideParentTenant ? 1 : 0);
                WriteStringOrNull(writer, "lane_name", row.Lane);
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
                writer.WriteString("tag_name", tag.Name);
                WriteStringOrNull(writer, "tag_value", tag.Value);
                WriteStringOrNull(writer, "tag_value_search", tag.ValueSearch);
            }
        );

        AddText(command, "@p_rows", jsonRows);
        AddText(command, "@p_tags", jsonTags);
    }

    public void BindEnqueueJob(DbCommand command, JobEnqueueValues row, Guid jobRef, string schema)
    {
        if (row.ParentJobId is null)
        {
            command.CommandText = WithoutSection(command.CommandText, "child-only");
        }

        AddText(command, "@p_job_ref", jobRef.ToString());
        AddText(command, "@p_namespace_name", row.NamespaceName);
        AddText(command, "@p_job_name", row.JobName);
        AddNullableText(command, "@p_deduplication_key", row.DeduplicationKey);
        AddNullableText(command, "@p_correlation_key", row.CorrelationKey);
        AddNullableInt(command, "@p_job_priority_code_override", row.PriorityOverride is { } priority ? (short)priority : null);
        AddInt(command, "@p_input_format_id", row.Input.Format.Id);
        AddNullableBlob(command, "@p_input", row.Input.Format.IsNone ? null : row.Input.Data.ToArray());
        AddNullableText(command, "@p_concurrency_key", row.ConcurrencyKey);
        AddNullableInt(command, "@p_next_run_at_utc", row.NextRunAtUtc is { } nextRun ? ToUnixMs(nextRun) : null);
        AddNullableInt(command, "@p_delay_seconds", row.DelaySeconds);
        AddNullableInt(command, "@p_parent_job_id", row.ParentJobId);
        AddNullableText(command, "@p_tenant_name", row.TenantName);
        AddInt(command, "@p_tenant_override", row.OverrideParentTenant ? 1 : 0);
        AddNullableText(command, "@p_lane_name", row.Lane);

        var jsonTags = JsonArray(
            row.Tags ?? [],
            (writer, tag, _) =>
            {
                writer.WriteString("tag_name", tag.Name);
                WriteStringOrNull(writer, "tag_value", tag.Value);
                WriteStringOrNull(writer, "tag_value_search", TagValueSearch.Normalize(tag.Value));
            }
        );
        AddText(command, "@p_tags", jsonTags);
    }

    /// <summary>
    /// Folds each <c>(@p IS NULL OR x)</c> catch-all against the bound value of <c>@p</c>. SQLite plans a statement
    /// without its parameter values, so an unfolded catch-all keeps its filter from ever using an index.
    /// </summary>
    public void PrepareInlineCommand(DbCommand command) =>
        command.CommandText = WithoutNullCatchAlls(command.CommandText, command.Parameters);

    /// <summary>
    /// The script with each <c>(@p IS NULL OR x)</c> made <c>(x)</c> when <c>@p</c> holds a value and <c>1</c> when it holds
    /// null, which is exact either way. Matching resumes where a fold was made, so a chain such as
    /// <c>(@a IS NULL OR @b IS NULL OR x)</c> folds whole. A catch-all whose parameter is not bound stays as written, so the
    /// driver still refuses the command instead of the filter quietly turning off.
    /// </summary>
    internal static string WithoutNullCatchAlls(string sql, DbParameterCollection parameters)
    {
        var head = NullCatchAllHead().Match(sql);
        while (head.Success)
        {
            var bound = parameters.IndexOf(head.Groups[1].Value);
            if (bound < 0)
            {
                head = head.NextMatch();
                continue;
            }

            var close = ClosingParenthesis(sql, head.Index);
            var filter = parameters[bound].Value is null or DBNull ? "1" : "(" + sql[(head.Index + head.Length)..close] + ")";
            sql = sql[..head.Index] + filter + sql[(close + 1)..];
            head = NullCatchAllHead().Match(sql, head.Index);
        }
        return sql;
    }

    /// <summary>
    /// The parenthesis that closes the one at <paramref name="open"/>, by depth alone: a parenthesis inside a literal or a
    /// comment within the body would fold to malformed SQL, which the driver refuses.
    /// </summary>
    private static int ClosingParenthesis(string sql, int open)
    {
        var depth = 0;
        for (var i = open; ; i++)
        {
            depth += sql[i] switch
            {
                '(' => 1,
                ')' => -1,
                _ => 0,
            };
            if (depth == 0)
            {
                return i;
            }
        }
    }

    // The keyset cursors spread their catch-all over lines, so any whitespace separates the words.
    [GeneratedRegex(@"\(\s*(@p_\w+)\s+IS\s+NULL\s+OR\s+")]
    private static partial Regex NullCatchAllHead();

    public void BindRegisterDefinitions(
        DbCommand command,
        int namespaceId,
        DateTime manifestGenerationUtc,
        IReadOnlyList<DefinitionValues> rows,
        string schema
    )
    {
        AddInt(command, "@p_namespace_id", namespaceId);
        AddInt(command, "@p_manifest_generation", ToUnixMs(manifestGenerationUtc));

        var definitions = JsonArray(
            rows,
            (writer, row, _) =>
            {
                writer.WriteString("definition_name", row.DefinitionName);
                writer.WriteNumber("job_priority_code", row.JobPriorityCode);
                writer.WriteNumber("max_attempts", row.MaxAttempts);
                WriteNumberOrNull(writer, "concurrency_limit", row.ConcurrencyLimit);
                WriteStringOrNull(writer, "rate_limit", row.RateLimit);
                WriteStringOrNull(writer, "rate_name", row.RateName);
                WriteStringOrNull(writer, "lane_name", row.LaneName);
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
                writer.WriteNumber("event_recording_code", row.EventRecordingCode);
                writer.WriteNumber("alert_profile_code", row.AlertProfileCode);
                writer.WriteNumber("tenant_requirement_code", row.TenantRequirementCode);
                WriteStringOrNull(writer, "alert_channel_name", row.AlertChannelName);
                WriteStringOrNull(writer, "runbook_url", row.RunbookUrl);
                WriteStringOrNull(writer, "display_name", row.DisplayName);
                WriteStringOrNull(writer, "description", row.Description);
                writer.WriteString("definition_hash", row.DefinitionHash);
            }
        );

        AddText(command, "@p_definitions", definitions);
    }

    public void BindRegisterSchedules(
        DbCommand command,
        IReadOnlyList<DefinitionSchedules> definitions,
        IReadOnlyList<Guid> slotRefs,
        string schema
    )
    {
        var definitionRows = JsonArray(
            definitions,
            (writer, definition, index) =>
            {
                writer.WriteNumber("definition_id", definition.DefinitionId);
                writer.WriteString("job_ref", slotRefs[index].ToString());
                writer.WriteNumber("input_format_id", definition.InputFormatId);
                WriteBase64OrNull(
                    writer,
                    "input",
                    definition.InputFormatId == JobPayloadFormat.None.Id ? (ReadOnlyMemory<byte>?)null : definition.Input
                );
                writer.WriteNumber("event_recording_code", (short)definition.EventRecording);
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
                writer.WriteString("schedule_name", item.Schedule.ScheduleName);
                writer.WriteString("expression", item.Schedule.Expression);
                writer.WriteString("time_zone_id", string.IsNullOrWhiteSpace(item.Schedule.TimeZoneId) ? "UTC" : item.Schedule.TimeZoneId);
                writer.WriteNumber("expression_kind_code", (short)item.Schedule.ExpressionKind);
                writer.WriteNumber("misfire_strategy_code", (short)item.Schedule.MisfireStrategy);
                WriteUtcOrNull(writer, "next_occurrence_at_utc", item.Schedule.NextOccurrenceAtUtc);
                WriteStringOrNull(writer, "description", item.Schedule.Description);
                WriteUtcOrNull(writer, "interval_anchor_at_utc", item.Schedule.IntervalAnchorAtUtc);
                writer.WriteString("timing_fingerprint", item.Schedule.TimingFingerprint);
            }
        );

        AddText(command, "@p_definitions", definitionRows);
        AddText(command, "@p_schedules", scheduleRows);
    }

    public void BindAdmitFirings(DbCommand command, AdmitFiringsCommand request)
    {
        AddText(command, "@p_groups", AdmitFiringsJson.Groups(request));
        AddText(command, "@p_rows", AdmitFiringsJson.Rows(request, static (writer, name, value) => WriteUtcOrNull(writer, name, value)));
    }

    /// <summary>Typed SQLite parameter primitives the dialect's own binders use.</summary>
    private static void AddText(DbCommand command, string name, string value) =>
        command.Parameters.Add(
            new SqliteParameter
            {
                ParameterName = name,
                SqliteType = SqliteType.Text,
                Value = value,
            }
        );

    private static void AddNullableText(DbCommand command, string name, string? value) =>
        command.Parameters.Add(
            new SqliteParameter
            {
                ParameterName = name,
                SqliteType = SqliteType.Text,
                Value = (object?)value ?? DBNull.Value,
            }
        );

    private static void AddInt(DbCommand command, string name, long value) =>
        command.Parameters.Add(
            new SqliteParameter
            {
                ParameterName = name,
                SqliteType = SqliteType.Integer,
                Value = value,
            }
        );

    private static void AddNullableInt(DbCommand command, string name, long? value) =>
        command.Parameters.Add(
            new SqliteParameter
            {
                ParameterName = name,
                SqliteType = SqliteType.Integer,
                Value = (object?)value ?? DBNull.Value,
            }
        );

    private static void AddNullableBlob(DbCommand command, string name, byte[]? value) =>
        command.Parameters.Add(
            new SqliteParameter
            {
                ParameterName = name,
                SqliteType = SqliteType.Blob,
                Value = (object?)value ?? DBNull.Value,
            }
        );

    /// <summary>
    /// JSON rowset writing: SQLite has no table-valued parameter, so batch binders pass a single JSON
    /// array text parameter that the SQL body expands with <c>json_each</c>. Distinct from scalar
    /// parameter binding - this composes the payload, it does not bind a command.
    /// </summary>
    private static string JsonArray<T>(IReadOnlyList<T> items, Action<Utf8JsonWriter, T, int> writeItem)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            for (var i = 0; i < items.Count; i++)
            {
                writer.WriteStartObject();
                writeItem(writer, items[i], i);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteStringOrNull(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteNumberOrNull(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void WriteUtcOrNull(Utf8JsonWriter writer, string name, DateTime? value)
    {
        if (value is { } instant)
        {
            writer.WriteNumber(name, ToUnixMs(instant));
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void WriteBase64OrNull(Utf8JsonWriter writer, string name, ReadOnlyMemory<byte>? value)
    {
        if (value is { } bytes)
        {
            writer.WriteString(name, Convert.ToBase64String(bytes.Span));
        }
        else
        {
            writer.WriteNull(name);
        }
    }
}
