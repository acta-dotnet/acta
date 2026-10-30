namespace Acta;

/// <summary>The Calendars operations: <see cref="ICalendars"/> for applications, plus the engine's calls.</summary>
internal sealed class CalendarsOperations(
    ListCalendarsHandler listCalendars,
    GetCalendarHandler getCalendar,
    SaveCalendarDraftHandler saveCalendarDraft,
    ArchiveCalendarHandler archiveCalendar,
    PreviewCalendarHandler previewCalendar,
    PublishCalendarHandler publishCalendar,
    GetCurrentCalendarsHandler getCurrentCalendars,
    ResolveCalendarVersionHandler getCalendarVersion,
    CalendarSources sources
) : ICalendars
{
    public ValueTask<ListCalendarsResponse> ListAsync(ListCalendarsQuery query, CancellationToken ct = default) =>
        listCalendars.HandleAsync(query, ct);

    public ValueTask<GetCalendarResponse?> GetAsync(CalendarLookup calendar, int? version = null, CancellationToken ct = default) =>
        getCalendar.HandleAsync(new GetCalendarQuery(calendar, version), ct);

    public ValueTask<SaveCalendarDraftResponse> SaveDraftAsync(
        CalendarLookup calendar,
        CalendarRoleCode role,
        string definition,
        int expectedLatestVersion,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    ) =>
        saveCalendarDraft.HandleAsync(
            new SaveCalendarDraftCommand(calendar, role, definition, expectedLatestVersion, reasonMessage, actor),
            ct
        );

    public async ValueTask<PreviewCalendarResponse?> PreviewAsync(
        CalendarLookup calendar,
        int version,
        DateTime? fromUtc = null,
        int count = 10,
        string? timeZoneId = null,
        CancellationToken ct = default
    ) => await previewCalendar.HandleAsync(new PreviewCalendarQuery(calendar, version, fromUtc, count, timeZoneId), ct);

    public async ValueTask<PublishCalendarResponse> PublishAsync(
        CalendarLookup calendar,
        int expectedCurrentVersion,
        int candidateVersion,
        OverdueDecisionCode overdue = OverdueDecisionCode.Refuse,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    ) =>
        await publishCalendar.HandleAsync(
            new PublishCalendarCommand(calendar, expectedCurrentVersion, candidateVersion, overdue, reasonMessage, actor),
            ct
        );

    public ValueTask<ArchiveCalendarResponse> ArchiveAsync(
        CalendarLookup calendar,
        int expectedCurrentVersion,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    ) => archiveCalendar.HandleAsync(new ArchiveCalendarCommand(calendar, expectedCurrentVersion, reasonMessage, actor), ct);

    /// <summary>The source of <paramref name="timing"/> from the code calendars and the current catalog versions the last preload read.</summary>
    internal ScheduleSource Source(int namespaceId, ScheduleTiming timing) => sources.Source(namespaceId, timing);

    /// <summary>The source of <paramref name="timing"/>, its calendar names resolved now.</summary>
    internal Task<ScheduleSource> SourceAsync(int namespaceId, ScheduleTiming timing, CancellationToken ct) =>
        sources.SourceAsync(namespaceId, timing, ct);

    /// <summary>Resolves <paramref name="names"/> against the current catalog in one read, so a pass plans without more.</summary>
    internal Task PreloadAsync(int namespaceId, IEnumerable<string> names, CancellationToken ct) =>
        sources.PreloadAsync(namespaceId, names, ct);

    /// <summary>The namespace's current calendars, one per family that has a Published version.</summary>
    internal Task<GetCurrentCalendarsResponse> GetCurrentCalendarsAsync(int namespaceId, CancellationToken ct) =>
        getCurrentCalendars.HandleAsync(new GetCurrentCalendarsQuery(namespaceId), ct);

    /// <summary>One version of a family whatever its status, with its definition; null when the family has no such version.</summary>
    internal Task<ResolveCalendarVersionResponse?> ResolveCalendarVersionAsync(
        int namespaceId,
        string calendarName,
        int version,
        CancellationToken ct
    ) => getCalendarVersion.HandleAsync(new ResolveCalendarVersionQuery(namespaceId, calendarName, version), ct);
}
