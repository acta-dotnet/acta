using System.Text;

namespace Acta;

/// <summary>Compiles the version and the current one and walks each from the same instant; no cursor is read or written.</summary>
internal sealed class PreviewCalendarHandler(IActaClock clock, GetCalendarPublicationHandler family, GetCalendarSchedulesHandler dependents)
{
    public async Task<PreviewCalendarResponse?> HandleAsync(PreviewCalendarQuery query, CancellationToken ct)
    {
        var (jobNamespace, calendarName) = CalendarControl.Names(query.Calendar);
        var zone = string.IsNullOrWhiteSpace(query.TimeZoneId) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(query.TimeZoneId);
        var rows = (await family.HandleAsync(new GetCalendarPublicationQuery(jobNamespace, calendarName, query.Version), ct)).Rows;
        if (rows.FirstOrDefault(r => r.Version == query.Version) is not { } version)
        {
            return null;
        }

        var current = rows.FirstOrDefault(r => r.Status == CalendarStatusCode.Published);
        var now = await clock.GetUtcNowAsync(ct);
        var from = DateTime.SpecifyKind(query.FromUtc ?? now, DateTimeKind.Utc);
        var count = Math.Clamp(query.Count, 1, 50);
        var compiled = Compile(version);
        var compiledCurrent = current is null ? null : Compile(current);
        var bound = (await dependents.HandleAsync(new GetCalendarSchedulesQuery(version.NamespaceId, calendarName), ct)).Schedules;
        return new PreviewCalendarResponse(
            version.Role,
            version.Version,
            current?.Version,
            Occurrences(compiled.Calendar, from, zone, count),
            Occurrences(compiledCurrent?.Calendar, from, zone, count),
            Windows(compiled.Availability, from, zone, count),
            Windows(compiledCurrent?.Availability, from, zone, count),
            [
                .. bound.Select(s => new PreviewCalendarItem(
                    s.NamespaceName,
                    s.DefinitionName,
                    s.ScheduleName,
                    version.Role,
                    s.Status,
                    version.Role == CalendarRoleCode.Occurrence ? s.CalendarVersion ?? 0 : s.AvailabilityVersion ?? 0,
                    s.Status != ScheduleStatusCode.Orphaned && s.NextOccurrenceAtUtc is { } due && due <= now
                )),
            ]
        );
    }

    private static CompiledCalendar Compile(FamilyVersion row) =>
        CalendarDefinition.Compile(row.Role, Encoding.UTF8.GetString(row.Definition.Span));

    private static List<DateTime> Occurrences(IActaCalendar? calendar, DateTime from, TimeZoneInfo zone, int count)
    {
        var instants = new List<DateTime>();
        var after = from;
        while (calendar is not null && instants.Count < count && calendar.GetNextOccurrence(new CalendarContext(after, zone)) is { } next)
        {
            instants.Add(next);
            after = next;
        }

        return instants;
    }

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
