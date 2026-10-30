using System.Data.Common;

namespace Acta;

/// <summary>What the schedule control verbs share.</summary>
internal static class ScheduleControl
{
    /// <summary>An operator's reason as the schedule row and the event store it, cut to the row's column.</summary>
    public static string? Reason(string? reasonMessage) => reasonMessage.Truncate(ActaTextLimits.SchedulePauseMessage);

    /// <summary>A schedule event's details: the operator, and the schedule by name, since a schedule has no numeric id.</summary>
    public static byte[]? Details(string scheduleName, string? actor) => EventDetails.Write(Facts(scheduleName, actor));

    /// <summary>The same details as text, for an apply that takes them as a string.</summary>
    public static string? DetailsText(string scheduleName, string? actor) => EventDetails.Text(Facts(scheduleName, actor));

    private static (string Name, string? Value)[] Facts(string scheduleName, string? actor) =>
        [("actor", JobControlActor.Operator(actor)), ("schedule", scheduleName)];

    /// <summary>
    /// Binds what pause and resume take after their own arguments: the event's message and details. Pause binds its
    /// row reason before this; resume clears the row's reason in its SQL.
    /// </summary>
    public static void BindTail(DbCommand cmd, ISqlDialect dialect, string scheduleName, string? actor, string? reasonMessage) =>
        cmd.AddEventFields(dialect, Reason(reasonMessage), Details(scheduleName, actor));
}
