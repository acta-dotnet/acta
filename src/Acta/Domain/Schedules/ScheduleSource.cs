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

    /// <summary>Why the occurrence calendar resolved to nothing, when the catalog holds a version of that name; null says none is published.</summary>
    public string? CalendarProblem { get; init; }

    /// <summary>Why the availability calendar resolved to nothing, when the catalog holds a version of that name; null says none is published.</summary>
    public string? AvailabilityProblem { get; init; }

    /// <summary>One sentence for each calendar the timing names that resolved to nothing; none when the source can be used.</summary>
    public IEnumerable<string> Problems()
    {
        if (Timing.ExpressionKind == ScheduleExpressionKindCode.Calendar && Calendar is null)
        {
            yield return CalendarProblem ?? Unpublished(CalendarRoleCode.Occurrence, Timing.Expression);
        }

        if (Timing.AvailabilityCalendarName is { } name && Availability is null)
        {
            yield return AvailabilityProblem ?? Unpublished(CalendarRoleCode.Availability, name);
        }
    }

    /// <summary>This source, or the error saying why the first calendar it names resolves to nothing.</summary>
    public ScheduleSource Bound() => Problems().FirstOrDefault() is { } problem ? throw new CalendarUnavailableException(problem) : this;

    private static string Unpublished(CalendarRoleCode role, string name) =>
        $"The {role.Code} calendar '{name}' has no published version in the namespace's calendar catalog or the shared one.";
}
