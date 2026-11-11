namespace Acta.AspNetCore;

/// <summary>
/// The calendar screen in one response: the family's head with one version's definition, and the first page of its
/// versions and of the schedules naming it, each with its filter-wide count and the cursor its own route continues from.
/// </summary>
/// <param name="Calendar">The calendar family's head.</param>
/// <param name="Versions">The first page of the family's versions.</param>
/// <param name="Dependents">The first page of the schedules that name the family.</param>
internal sealed record GetCalendarDetailResponse(
    GetCalendarResponse Calendar,
    ListCalendarVersionsResponse Versions,
    ListCalendarDependentsResponse Dependents
)
{
    /// <summary>The detail of <paramref name="calendar"/> showing <paramref name="version"/>, or null when the family or the version is missing.</summary>
    public static async ValueTask<GetCalendarDetailResponse?> ComposeAsync(
        ICalendars calendars,
        CalendarLookup calendar,
        long? version,
        CancellationToken ct
    ) =>
        await calendars.GetAsync(calendar, version, ct) is { } head
            ? new GetCalendarDetailResponse(
                GetCalendarResponse.From(head),
                await calendars.ListVersionsAsync(new ListCalendarVersionsQuery(calendar, IncludeTotal: true), ct),
                await calendars.ListDependentsAsync(new ListCalendarDependentsQuery(calendar, IncludeTotal: true), ct)
            )
            : null;
}
