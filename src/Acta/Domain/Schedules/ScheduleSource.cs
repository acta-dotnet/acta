namespace Acta;

/// <summary>
/// A schedule's timing with the calendars it names resolved: the occurrence calendar when the kind is Calendar, the
/// availability calendar when one is named, each null when the name resolves to nothing here. The calculator and the
/// evaluator take a source; <see cref="CalendarRegistry"/> builds one.
/// </summary>
internal sealed record ScheduleSource(ScheduleTiming Timing, IActaCalendar? Calendar, IActaAvailabilityCalendar? Availability)
{
    /// <summary>A cron or interval timing with no calendars, which needs no registry.</summary>
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
        $"The {role} '{name}' is not declared in this process and has no published version in the namespace's calendar catalog or the shared one.";
}
