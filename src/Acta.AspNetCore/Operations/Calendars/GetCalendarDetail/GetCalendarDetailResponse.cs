namespace Acta.AspNetCore;

/// <summary>
/// The calendar screen in one response: the family's head with one version's definition, and the first page of its
/// versions with its filter-wide count and the cursor its own route continues from.
/// </summary>
internal sealed record GetCalendarDetailResponse(GetCalendarResponse Calendar, ListCalendarVersionsResponse Versions)
{
    /// <summary>The detail of <paramref name="calendar"/> showing <paramref name="version"/>, or null when the family or the version is missing.</summary>
    public static async ValueTask<GetCalendarDetailResponse?> ComposeAsync(
        ICalendars calendars,
        CalendarLookup calendar,
        int? version,
        CancellationToken ct
    ) =>
        await calendars.GetAsync(calendar, version, ct) is { } head
            ? new GetCalendarDetailResponse(
                head,
                await calendars.ListVersionsAsync(new ListCalendarVersionsQuery(calendar, IncludeTotal: true), ct)
            )
            : null;
}
