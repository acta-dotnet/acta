using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Acta;

/// <summary>
/// Parameter spec helpers: build a <see cref="DbParameterSpec"/> from generated column /
/// value metadata, coerce its value to the driver-expected CLR type, and validate the spec
/// at bind time so under-specified metadata surfaces deterministically.
/// </summary>
internal static class DbParams
{
    public static DbParameterSpec For<T>(DbColumnSpec<T> spec, object? value) =>
        new(Name: spec.ParameterName, Value: value, Kind: spec.Kind, Size: spec.Size);

    public static DbParameterSpec For<T>(DbValueSpec<T> spec, T value) =>
        new(Name: spec.ParameterName, Value: value, Kind: spec.Kind, Size: spec.Size);

    public static object Coerce(DbParameterSpec p)
    {
        if (p.Value is null)
        {
            return DBNull.Value;
        }

        if (p.Kind == DbKind.UtcInstant && p.Value is DateTime dt)
        {
            return ToUtc(dt);
        }

        if (p.Kind == DbKind.Byte && p.Value is not byte)
        {
            return Convert.ToByte(p.Value, CultureInfo.InvariantCulture);
        }
        if (p.Kind == DbKind.Int16 && p.Value is not short)
        {
            return Convert.ToInt16(p.Value, CultureInfo.InvariantCulture);
        }
        if (p.Kind == DbKind.Int32 && p.Value is not int)
        {
            return Convert.ToInt32(p.Value, CultureInfo.InvariantCulture);
        }
        return p.Kind == DbKind.Int64 && p.Value is not long ? Convert.ToInt64(p.Value, CultureInfo.InvariantCulture) : p.Value;
    }

    /// <summary>
    /// Normalizes a caller-supplied instant to UTC for the bulk binders, which build provider rows
    /// directly and bypass <see cref="Coerce"/>: Local is converted, Unspecified is tagged UTC.
    /// </summary>
    public static DateTime ToUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

    /// <summary>
    /// JSON array text (<c>[1,2]</c>) for the dialects whose SQL expands an id set with OPENJSON or
    /// json_each; null for an empty set, which those bodies bind as DBNull and read as "no filter".
    /// </summary>
    public static string? JsonIdArray(int[]? values)
    {
        if (values is not { Length: > 0 })
        {
            return null;
        }

        var text = new StringBuilder("[");
        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0)
            {
                text.Append(',');
            }
            text.Append(values[i].ToString(CultureInfo.InvariantCulture));
        }
        return text.Append(']').ToString();
    }

    /// <summary>
    /// The held executions as JSON array text (<c>[{"job_id":1,"execution_number":2}]</c>) for the dialects whose
    /// lease renewal expands them with OPENJSON or json_each.
    /// </summary>
    public static string JsonHeldExecutions(IReadOnlyList<(long JobId, int ExecutionNumber)> executions)
    {
        var text = new StringBuilder("[");
        for (var i = 0; i < executions.Count; i++)
        {
            if (i > 0)
            {
                text.Append(',');
            }
            text.Append(
                CultureInfo.InvariantCulture,
                $"{{\"job_id\":{executions[i].JobId},\"execution_number\":{executions[i].ExecutionNumber}}}"
            );
        }
        return text.Append(']').ToString();
    }

    /// <summary>
    /// The held lock holds as JSON array text (<c>[{"lock_key":"k","hold_token":"..."}]</c>), the token in the "D"
    /// format SQLite stores it in, for the dialects whose lease renewal expands them with OPENJSON or json_each.
    /// </summary>
    public static string JsonHeldLocks(IReadOnlyList<LockToken> locks)
    {
        var text = new StringBuilder("[");
        for (var i = 0; i < locks.Count; i++)
        {
            if (i > 0)
            {
                text.Append(',');
            }
            text.Append(
                CultureInfo.InvariantCulture,
                $"{{\"lock_key\":\"{JsonEncodedText.Encode(locks[i].Key)}\",\"hold_token\":\"{locks[i].HoldToken:D}\"}}"
            );
        }
        return text.Append(']').ToString();
    }

    /// <summary>
    /// A batch's completions as JSON array text for SQLite's batch completion, which expands them with jsonb_each: each
    /// result as base64, and a null field left out, since every row is parsed and a missing key reads as null.
    /// </summary>
    public static string JsonCompletions(IReadOnlyList<CompleteExecutionsValues> completions)
    {
        var text = new StringBuilder("[");
        for (var i = 0; i < completions.Count; i++)
        {
            var c = completions[i];
            text.Append(i > 0 ? ",{" : "{");
            text.Append(CultureInfo.InvariantCulture, $"\"job_id\":{c.JobId},\"execution_number\":{c.ExecutionNumber}");
            text.Append(
                CultureInfo.InvariantCulture,
                $",\"succeeded\":{(c.Succeeded ? "true" : "false")},\"result_format_id\":{c.ResultFormatId}"
            );
            text.Append(CultureInfo.InvariantCulture, $",\"result\":\"{Convert.ToBase64String(c.Result.Span)}\"");
            Field(text, "reason_code", (byte?)c.EventReasonCode);
            Field(text, "reason_message", c.ReasonMessage);
            Field(text, "duration_ms", c.DurationMs);
            Field(text, "failure_count", c.FailureCount);
            Field(text, "retention_seconds", c.RetentionSeconds);
            Field(text, "reschedule_status_code", c.RescheduleStatusCode);
            Field(text, "reschedule_delay_seconds", c.RescheduleDelaySeconds);
            Field(text, "reschedule_resume_at_utc", UnixMs(c.RescheduleResumeAtUtc));
            Field(text, "wait_signal_name", c.WaitSignalName);
            Field(text, "handler_status_code", c.HandlerStatusCode);
            Field(text, "final_status", (byte?)c.FinalStatus);
            Field(text, "job_next_run_at_utc", UnixMs(c.JobNextRunAtUtc));
            text.Append('}');
        }
        return text.Append(']').ToString();
    }

    private static void Field(StringBuilder text, string name, long? value)
    {
        if (value is { } number)
        {
            text.Append(CultureInfo.InvariantCulture, $",\"{name}\":{number}");
        }
    }

    private static void Field(StringBuilder text, string name, string? value)
    {
        if (value is not null)
        {
            text.Append(CultureInfo.InvariantCulture, $",\"{name}\":\"{JsonEncodedText.Encode(value)}\"");
        }
    }

    private static long? UnixMs(DateTime? value) =>
        value is { } instant ? new DateTimeOffset(ToUtc(instant)).ToUnixTimeMilliseconds() : null;

    public static void Validate(DbParameterSpec p)
    {
        if (string.IsNullOrWhiteSpace(p.Name))
        {
            throw new InvalidOperationException("SQL parameter name is required.");
        }

        if (p.Kind is DbKind.AsciiString or DbKind.UnicodeString && p.Size is null)
        {
            throw new InvalidOperationException(
                $"SQL parameter '{p.Name}' (DbKind.{p.Kind}) requires explicit Size: bind via DbParams.For(...) with generated metadata."
            );
        }
    }
}
