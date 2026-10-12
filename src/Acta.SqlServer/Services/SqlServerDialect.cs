using System.Data;
using System.Data.Common;
using System.Globalization;
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
        SqlIdentifierSyntax.ValidateBareIdentifier(routineName, nameof(routineName));
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

    public void BindHeldLeases(
        DbCommand command,
        IReadOnlyList<(long JobId, int ExecutionNumber)> executions,
        IReadOnlyList<LockToken> locks
    )
    {
        var sql = (SqlCommand)command;
        AddScalar(sql, "@p_executions", SqlDbType.NVarChar, DbParams.JsonHeldExecutions(executions));
        AddScalar(sql, "@p_locks", SqlDbType.NVarChar, DbParams.JsonHeldLocks(locks));
    }

    public void BindCompleteExecutions(DbCommand command, CompleteExecutionsCommand request, string schema)
    {
        var sql = (SqlCommand)command;
        AddParameter(
            sql,
            "@p_batch",
            SqlDbType.Structured,
            SqlServerTableParameters.BuildCompleteExecutionsRecords(request.Completions),
            typeName: $"{schema}.complete_executions_batch"
        );

        // JSON text rather than a second table-valued parameter, which SQL Server would build on every call even empty.
        var advances = request.Completions.SelectMany(c => c.ScheduleAdvances ?? []).ToList();
        if (advances.Count > 0)
        {
            AddParameter(sql, "@p_schedule_advances", SqlDbType.NVarChar, JsonScheduleAdvances(advances), size: -1);
        }
    }

    public void BindEnqueueJobs(DbCommand command, IReadOnlyList<JobEnqueueValues> rows, IReadOnlyList<Guid> jobRefs, string schema)
    {
        var sql = (SqlCommand)command;
        sql.Parameters.Add(
            new SqlParameter
            {
                ParameterName = "@p_batch",
                SqlDbType = SqlDbType.Structured,
                TypeName = $"{schema}.enqueue_jobs_batch",
                Value = SqlServerTableParameters.BuildBatchRecords(rows, jobRefs),
            }
        );
        sql.Parameters.Add(
            new SqlParameter
            {
                ParameterName = "@p_tag_batch",
                SqlDbType = SqlDbType.Structured,
                TypeName = $"{schema}.enqueue_tags_batch",
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
        AddScalar(sql, "@p_tenant_name", SqlDbType.VarChar, (object?)row.TenantName ?? DBNull.Value);
        AddScalar(sql, "@p_tenant_override", SqlDbType.Bit, row.OverrideParentTenant);
        AddScalar(sql, "@p_lane", SqlDbType.VarChar, (object?)row.Lane ?? DBNull.Value);
        sql.Parameters.Add(
            new SqlParameter
            {
                ParameterName = "@p_tag_batch",
                SqlDbType = SqlDbType.Structured,
                TypeName = $"{schema}.enqueue_tags_batch",
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
                TypeName = $"{schema}.register_definitions_batch",
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
                TypeName = $"{schema}.register_slots_batch",
                Value = SqlServerTableParameters.BuildSlotRecords(definitions, slotRefs),
            }
        );
        sql.Parameters.Add(
            new SqlParameter
            {
                ParameterName = "@p_schedules",
                SqlDbType = SqlDbType.Structured,
                TypeName = $"{schema}.register_schedules_batch",
                Value = SqlServerTableParameters.BuildScheduleUpsertRecords(definitions),
            }
        );
    }

    /// <summary>The batch's schedule advances as JSON text, each instant as an ISO-8601 UTC string, for OPENJSON.</summary>
    private static string JsonScheduleAdvances(IReadOnlyList<ScheduleAdvance> advances) =>
        "["
        + string.Join(
            ",",
            advances.Select(advance =>
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{{\"schedule_id\":{advance.ScheduleId},\"next_run_at_utc\":{(advance.NextRunAtUtc is { } next ? $"\"{DbParams.ToUtc(next):yyyy-MM-ddTHH:mm:ss.fff}\"" : "null")},\"expected_version\":{advance.ExpectedVersion?.ToString(CultureInfo.InvariantCulture) ?? "null"}}}"
                )
            )
        )
        + "]";

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
