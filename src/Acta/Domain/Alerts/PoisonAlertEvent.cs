using System.Globalization;

namespace Acta;

/// <summary>
/// An event the <c>sys.alerts</c> projector skipped as poison, recorded on its slot as the variable
/// <c>alerts-skip-{EventId}</c> whose durable record names the job and event position. Until replay disposes of it,
/// retention keeps the event and its job past the alert floor, and an explicit purge of the job is refused.
/// </summary>
internal readonly record struct PoisonAlertEvent(long EventId, long JobId, long Ticks)
{
    /// <summary>The variable-name prefix of a skip record; the event id follows it in invariant digits.</summary>
    public const string VariablePrefix = "alerts-skip-";

    private const string JobField = ";jobId=";
    private const string TicksField = ";ticks=";

    /// <summary>The variable that records this skip.</summary>
    public string VariableName => VariablePrefix + EventId.ToString(CultureInfo.InvariantCulture);

    /// <summary>The durable record an operator reads: where, which event and job, why, and the exception's detail.</summary>
    public string Record(string jobNamespace, string reason, string exceptionName, string detail) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"namespace={jobNamespace};eventId={EventId}{JobField}{JobId}{TicksField}{Ticks};reason={reason};exception={exceptionName};detail={detail}"
        );

    /// <summary>
    /// The skip a variable records, or null when its name or position is unreadable.
    /// </summary>
    public static PoisonAlertEvent? Parse(string variableName, string? record)
    {
        if (
            !variableName.StartsWith(VariablePrefix, StringComparison.Ordinal)
            || !long.TryParse(variableName.AsSpan(VariablePrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var eventId)
        )
        {
            return null;
        }

        if (!ReadField(record, JobField, out var jobId) || !ReadField(record, TicksField, out var ticks) || ticks > DateTime.MaxValue.Ticks)
        {
            return null;
        }

        return new PoisonAlertEvent(eventId, jobId, ticks);
    }

    private static bool ReadField(string? record, string field, out long value)
    {
        value = 0;
        var at = record?.IndexOf(field, StringComparison.Ordinal) ?? -1;
        if (at < 0)
        {
            return false;
        }

        var digits = record.AsSpan(at + field.Length);
        var end = digits.IndexOf(';');
        return long.TryParse(end < 0 ? digits : digits[..end], NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
