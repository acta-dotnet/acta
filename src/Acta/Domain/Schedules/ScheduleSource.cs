namespace Acta;

/// <summary>
/// A schedule's timing with the calendars it names resolved: the occurrence calendar when the kind is Calendar, the
/// availability calendar when one is named, each null when the name has no published catalog version. The calculator
/// and the evaluator take a source; the catalog's resolution builds one.
/// </summary>
internal sealed record ScheduleSource(ScheduleTiming Timing, IActaCalendar? Calendar, IActaAvailabilityCalendar? Availability)
{
    /// <summary>A cron or interval timing with no calendars, which needs no catalog.</summary>
    public static ScheduleSource Native(string expression, string? timeZoneId, ScheduleExpressionKindCode kind) =>
        new(new ScheduleTiming(expression, kind, timeZoneId, null), null, null);

    /// <summary>This source, or the error naming the calendar that resolves to nothing.</summary>
    public ScheduleSource Bound()
    {
        if (Timing.ExpressionKind == ScheduleExpressionKindCode.Calendar && Calendar is null)
        {
            throw new CalendarUnavailableException(Missing("calendar", Timing.Expression));
        }

        if (Timing.AvailabilityCalendarName is { } name && Availability is null)
        {
            throw new CalendarUnavailableException(Missing("availability calendar", name));
        }

        return this;
    }

    private static string Missing(string role, string name) =>
        $"The {role} '{name}' has no published version in the namespace's calendar catalog or the shared one.";
}
