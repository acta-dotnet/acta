namespace Acta.Tests.AspNetCore.Fakes;

/// <summary>
/// <see cref="ICalendars"/> over two families, billing/office-hours (availability, versions 1 and 2) and the shared
/// sys/holidays, staged by calendar name: "missing" reads as absent and its verbs as not found, "rejected" refuses every
/// verb, and an expected version of 999 is the stale one. Every other name applies, and every call is recorded.
/// </summary>
internal sealed class FakeCalendars : ICalendars
{
    private static readonly DateTime At = new(2026, 6, 12, 9, 0, 0, DateTimeKind.Utc);

    public FakeCalendars()
    {
        List = (query, _) =>
        {
            ListQueries.Add(query);
            IReadOnlyList<ListCalendarsItem> items = query.JobNamespace is null or "billing" ? [OfficeHours, Holidays] : [Holidays];
            return ValueTask.FromResult(new ListCalendarsResponse(items, null, false, 50, null));
        };
        Get = (calendar, version, _) =>
        {
            Reads.Add((calendar, version));
            return ValueTask.FromResult(Known(calendar, version ?? 1) ? Family(calendar, version ?? 1) : null);
        };
        Preview = (calendar, version, fromUtc, count, timeZoneId, _) =>
        {
            Previews.Add((calendar, version, fromUtc, count, timeZoneId));
            return ValueTask.FromResult(
                Known(calendar, version)
                    ? new PreviewCalendarResponse(CalendarRoleCode.Availability, version, 1, [], [], [new(At, At.AddHours(3))], [], [])
                    : null
            );
        };
        SaveDraft = (calendar, role, definition, expectedLatestVersion, reason, actor, _) =>
        {
            Drafts.Add((calendar, role, definition, expectedLatestVersion, reason, actor));
            return ValueTask.FromResult(
                calendar.CalendarName switch
                {
                    "missing" => new SaveCalendarDraftResponse(ControlAction.NotFound, null, null, []),
                    "rejected" => new SaveCalendarDraftResponse(ControlAction.Rejected, null, null, ["$.weekly[0].days[0]: unknown day."]),
                    _ when expectedLatestVersion == 999 => new SaveCalendarDraftResponse(
                        ControlAction.VersionConflict,
                        2,
                        null,
                        ["The family's latest version is 2, not 999."]
                    ),
                    _ => new SaveCalendarDraftResponse(ControlAction.Applied, expectedLatestVersion + 1, "hash-3", []),
                }
            );
        };
        Publish = (calendar, expectedCurrentVersion, candidateVersion, overdue, reason, actor, _) =>
        {
            Publications.Add((calendar, expectedCurrentVersion, candidateVersion, overdue, reason, actor));
            return ValueTask.FromResult(
                calendar.CalendarName switch
                {
                    "missing" => new PublishCalendarResponse(ControlAction.NotFound, null, [], [], null),
                    "rejected" => new PublishCalendarResponse(
                        ControlAction.Rejected,
                        null,
                        [],
                        ["send-invoice/nightly"],
                        "A bound schedule holds an overdue occurrence."
                    ),
                    _ when expectedCurrentVersion == 999 => new PublishCalendarResponse(
                        ControlAction.VersionConflict,
                        1,
                        [],
                        [],
                        "The current published version is 1, not 999."
                    ),
                    _ => new PublishCalendarResponse(ControlAction.Applied, candidateVersion, ["send-invoice/nightly"], [], null),
                }
            );
        };
        Archive = (calendar, expectedCurrentVersion, reason, actor, _) =>
        {
            Archives.Add((calendar, expectedCurrentVersion, reason, actor));
            return ValueTask.FromResult(
                calendar.CalendarName switch
                {
                    "missing" => new ArchiveCalendarResponse(ControlAction.NotFound, null, 0),
                    "rejected" => new ArchiveCalendarResponse(ControlAction.Rejected, 1, 2),
                    _ => new ArchiveCalendarResponse(ControlAction.Applied, expectedCurrentVersion, 0),
                }
            );
        };
    }

    public List<ListCalendarsQuery> ListQueries { get; } = [];

    public List<(CalendarLookup Calendar, int? Version)> Reads { get; } = [];

    public List<(CalendarLookup Calendar, int Version, DateTime? FromUtc, int Count, string? TimeZoneId)> Previews { get; } = [];

    public List<(
        CalendarLookup Calendar,
        CalendarRoleCode Role,
        string Definition,
        int ExpectedLatestVersion,
        string? Reason,
        string? Actor
    )> Drafts { get; } = [];

    public List<(
        CalendarLookup Calendar,
        int ExpectedCurrentVersion,
        int CandidateVersion,
        OverdueDecisionCode Overdue,
        string? Reason,
        string? Actor
    )> Publications { get; } = [];

    public List<(CalendarLookup Calendar, int ExpectedCurrentVersion, string? Reason, string? Actor)> Archives { get; } = [];

    public Func<ListCalendarsQuery, CancellationToken, ValueTask<ListCalendarsResponse>> List { get; set; }

    public Func<CalendarLookup, int?, CancellationToken, ValueTask<GetCalendarResponse?>> Get { get; set; }

    public Func<CalendarLookup, int, DateTime?, int, string?, CancellationToken, ValueTask<PreviewCalendarResponse?>> Preview { get; set; }

    public Func<
        CalendarLookup,
        CalendarRoleCode,
        string,
        int,
        string?,
        string?,
        CancellationToken,
        ValueTask<SaveCalendarDraftResponse>
    > SaveDraft { get; set; }

    public Func<
        CalendarLookup,
        int,
        int,
        OverdueDecisionCode,
        string?,
        string?,
        CancellationToken,
        ValueTask<PublishCalendarResponse>
    > Publish { get; set; }

    public Func<CalendarLookup, int, string?, string?, CancellationToken, ValueTask<ArchiveCalendarResponse>> Archive { get; set; }

    ValueTask<ListCalendarsResponse> ICalendars.ListAsync(ListCalendarsQuery query, CancellationToken ct) => List(query, ct);

    ValueTask<GetCalendarResponse?> ICalendars.GetAsync(CalendarLookup calendar, int? version, CancellationToken ct) =>
        Get(calendar, version, ct);

    ValueTask<SaveCalendarDraftResponse> ICalendars.SaveDraftAsync(
        CalendarLookup calendar,
        CalendarRoleCode role,
        string definition,
        int expectedLatestVersion,
        string? reasonMessage,
        string? actor,
        CancellationToken ct
    ) => SaveDraft(calendar, role, definition, expectedLatestVersion, reasonMessage, actor, ct);

    ValueTask<PreviewCalendarResponse?> ICalendars.PreviewAsync(
        CalendarLookup calendar,
        int version,
        DateTime? fromUtc,
        int count,
        string? timeZoneId,
        CancellationToken ct
    ) => Preview(calendar, version, fromUtc, count, timeZoneId, ct);

    ValueTask<PublishCalendarResponse> ICalendars.PublishAsync(
        CalendarLookup calendar,
        int expectedCurrentVersion,
        int candidateVersion,
        OverdueDecisionCode overdue,
        string? reasonMessage,
        string? actor,
        CancellationToken ct
    ) => Publish(calendar, expectedCurrentVersion, candidateVersion, overdue, reasonMessage, actor, ct);

    ValueTask<ArchiveCalendarResponse> ICalendars.ArchiveAsync(
        CalendarLookup calendar,
        int expectedCurrentVersion,
        string? reasonMessage,
        string? actor,
        CancellationToken ct
    ) => Archive(calendar, expectedCurrentVersion, reasonMessage, actor, ct);

    private static bool Known(CalendarLookup calendar, int version) => calendar.CalendarName != "missing" && version is 1 or 2;

    private static GetCalendarResponse Family(CalendarLookup calendar, int shownVersion) =>
        new(
            calendar.JobNamespace,
            calendar.CalendarName,
            CalendarRoleCode.Availability,
            1,
            [new(2, CalendarStatusCode.Draft, "hash-2", At, null, At), new(1, CalendarStatusCode.Published, "hash-1", At, At, At)],
            shownVersion,
            """{"kind":"weekly-availability"}""",
            [
                new(
                    "billing",
                    "send-invoice",
                    "nightly",
                    CalendarRoleCode.Availability,
                    ScheduleStatusCode.Active,
                    1,
                    ScheduledDeliveryCode.Permanent
                ),
            ]
        );

    private static readonly ListCalendarsItem OfficeHours = new(
        "billing",
        "office-hours",
        CalendarRoleCode.Availability,
        1,
        2,
        CalendarStatusCode.Draft,
        At,
        At,
        1
    );

    private static readonly ListCalendarsItem Holidays = new(
        "sys",
        "holidays",
        CalendarRoleCode.Occurrence,
        1,
        1,
        CalendarStatusCode.Published,
        At,
        At,
        0
    );
}
