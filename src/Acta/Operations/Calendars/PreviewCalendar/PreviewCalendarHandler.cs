using System.Text;

namespace Acta;

/// <summary>Compiles the version and the current one and walks each from the same instant; no cursor is read or written.</summary>
internal sealed class PreviewCalendarHandler(
    IActaClock clock,
    GetCalendarPublicationHandler family,
    ListCalendarSchedulesHandler dependents
)
{
    /// <summary>The bound schedules a preview lists, overdue ones first; <see cref="ICalendars.ListDependentsAsync"/> pages them all.</summary>
    internal const int ScheduleLimit = 100;

    public async Task<PreviewCalendarResponse?> HandleAsync(PreviewCalendarQuery query, CancellationToken ct)
    {
        var (jobNamespace, calendarName) = CalendarControl.Names(query.Calendar);
        var zone = NextOccurrenceCalculator.ResolveTimeZone(query.TimeZoneId);
        var (current, candidate) = await family.HandleAsync(new GetCalendarPublicationQuery(jobNamespace, calendarName, query.Version), ct);
        if (candidate is not { } version)
        {
            return null;
        }

        var now = await clock.GetUtcNowAsync(ct);
        var from = DateTime.SpecifyKind(query.FromUtc ?? now, DateTimeKind.Utc);
        var count = Math.Clamp(query.Count, 1, NextOccurrenceCalculator.MaxWalk);
        var compiled = Compile(version);
        var compiledCurrent = current is null ? null : Compile(current);
        var bound = (await dependents.HandleAsync(new ListCalendarSchedulesQuery(version.NamespaceId, calendarName), ct)).Schedules;
        var schedules = bound
            .Select(s => new PreviewCalendarItem(
                s.NamespaceName,
                s.DefinitionName,
                s.ScheduleName,
                version.Role,
                s.Status,
                s.Status != ScheduleStatusCode.Orphaned && s.NextOccurrenceAtUtc is { } due && due <= now
            ))
            .OrderByDescending(static item => item.Overdue)
            .ToList();
        return new PreviewCalendarResponse(
            version.Role,
            version.Version,
            current?.Version,
            Occurrences(compiled.Calendar, calendarName, query.TimeZoneId, from, count),
            Occurrences(compiledCurrent?.Calendar, calendarName, query.TimeZoneId, from, count),
            Windows(compiled.Availability, from, zone, count),
            Windows(compiledCurrent?.Availability, from, zone, count),
            [.. schedules.Take(ScheduleLimit)],
            schedules.Count,
            schedules.Count(static item => item.Overdue)
        );
    }

    private static CompiledCalendar Compile(FamilyVersion row) => CalendarDefinition.Compile(row.Role, row.Definition);

    /// <summary>The calendar walked as a schedule bound to it alone would be, under the calculator's contract checks.</summary>
    private static IReadOnlyList<DateTime> Occurrences(
        IActaCalendar? calendar,
        string calendarName,
        string? timeZoneId,
        DateTime from,
        int count
    ) =>
        calendar is null
            ? []
            : NextOccurrenceCalculator.Walk(
                new ScheduleSource(new ScheduleTiming(calendarName, ScheduleExpressionKindCode.Calendar, timeZoneId, null), calendar, null),
                from,
                count
            );

    private static List<CalendarWindow> Windows(IActaAvailabilityCalendar? availability, DateTime from, TimeZoneInfo zone, int count)
    {
        var windows = new List<CalendarWindow>();
        var at = from;
        while (
            availability is not null && windows.Count < count && availability.GetWindowAtOrAfter(new AvailabilityContext(at, zone)) is { } w
        )
        {
            windows.Add(w);
            if (w.EndUtc is not { } end)
            {
                break;
            }

            at = end;
        }

        return windows;
    }
}
