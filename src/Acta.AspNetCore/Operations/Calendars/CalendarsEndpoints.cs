using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore;

/// <summary>
/// The Calendars routes: the catalog list, one family, its versions and the schedules naming it, the calendar screen, a
/// version's preview, and save draft, publish, and archive. A family is addressed by its namespace and calendar name; the namespace <c>sys</c> holds
/// the shared families.
/// </summary>
internal static class CalendarsEndpoints
{
    public static void Map(RouteGroupBuilder reads, RouteGroupBuilder controls, ActaEndpointOptions options)
    {
        reads.MapList(
            "/calendars",
            new("listCalendars", ActaPermissions.Read),
            "List calendar families, the shared ones in sys included.",
            ActaApiJsonContext.Relaxed.ListCalendarsResponse,
            static query => new ListCalendarsQuery(
                JobNamespace: query.Text("jobNamespace"),
                Role: query.Code("role", CalendarRoleCodeExtensions.FromCode),
                NameContains: query.Text("nameContains"),
                LatestStatus: query.Code("latestStatus", CalendarStatusCodeExtensions.FromCode),
                PageSize: query.Int("pageSize"),
                Cursor: query.Text("cursor"),
                IncludeTotal: query.Bool("includeTotal") ?? false
            ),
            static (call, query) => call.Operations.Calendars.ListAsync(query, call.Ct),
            [
                new QueryParameterDoc(
                    "jobNamespace",
                    QueryParameterKind.String,
                    "Only this namespace's families and the shared ones in sys; omit for every namespace."
                ),
                new QueryParameterDoc("role", QueryParameterKind.String, "Exact calendar role as a kebab code.", CodeKind: "calendar-role"),
                new QueryParameterDoc(
                    "nameContains",
                    QueryParameterKind.String,
                    "A dotted kebab fragment matched anywhere in the calendar name."
                ),
                new QueryParameterDoc(
                    "latestStatus",
                    QueryParameterKind.String,
                    "Only the families whose latest version has this status, as a kebab code: draft lists the families with a version waiting to be published.",
                    CodeKind: "calendar-status"
                ),
                .. QueryParameterDocExtensions.PagingCore,
                .. QueryParameterDocExtensions.IncludeTotal,
            ]
        );
        reads
            .MapDetail(
                "/calendars/{jobNamespace}/{calendarName}",
                new("getCalendar", ActaPermissions.Read),
                "Read a calendar family's head: its current and latest versions and one definition.",
                "Calendar",
                ActaApiJsonContext.Relaxed.GetCalendarResponse,
                static async (http, call) =>
                    await call.Operations.Calendars.GetAsync(
                        Key(http),
                        QueryReader.Bind(http.Request, static query => query.Long("version")),
                        call.Ct
                    )
                        is { } calendar
                        ? GetCalendarResponse.From(calendar)
                        : null
            )
            .WithQueryParameters([
                new QueryParameterDoc(
                    "version",
                    QueryParameterKind.Long,
                    "The version whose definition to show; omit for the current version, else the latest."
                ),
            ]);
        GetCalendarDetailEndpoint.Map(reads, Key);
        reads
            .MapList(
                "/calendars/{jobNamespace}/{calendarName}/versions",
                new("listCalendarVersions", ActaPermissions.Read),
                "List a calendar family's versions, newest first.",
                ActaApiJsonContext.Relaxed.ListCalendarVersionsResponse,
                static query => new ListCalendarVersionsQuery(
                    Key(query.Http),
                    query.Int("pageSize"),
                    query.Text("cursor"),
                    query.Bool("includeTotal") ?? false
                ),
                static async (call, query) =>
                    await FamilyExistsAsync(call, query.Calendar)
                        ? await call.Operations.Calendars.ListVersionsAsync(query, call.Ct)
                        : throw new TargetNotFoundException("Calendar"),
                [.. QueryParameterDocExtensions.PagingCore, .. QueryParameterDocExtensions.IncludeTotal]
            )
            .ProducesProblem(StatusCodes.Status404NotFound);
        reads
            .MapList(
                "/calendars/{jobNamespace}/{calendarName}/dependents",
                new("listCalendarDependents", ActaPermissions.Read),
                "List the schedules naming a calendar family, by path.",
                ActaApiJsonContext.Relaxed.ListCalendarDependentsResponse,
                static query => new ListCalendarDependentsQuery(
                    Key(query.Http),
                    query.Int("pageSize"),
                    query.Text("cursor"),
                    query.Bool("includeTotal") ?? false
                ),
                static async (call, query) =>
                    await FamilyExistsAsync(call, query.Calendar)
                        ? await call.Operations.Calendars.ListDependentsAsync(query, call.Ct)
                        : throw new TargetNotFoundException("Calendar"),
                [.. QueryParameterDocExtensions.PagingCore, .. QueryParameterDocExtensions.IncludeTotal]
            )
            .ProducesProblem(StatusCodes.Status404NotFound);
        reads
            .MapDetail(
                "/calendars/{jobNamespace}/{calendarName}/preview",
                new("previewCalendar", ActaPermissions.Read),
                "Preview a calendar version beside the current one.",
                "Calendar",
                ActaApiJsonContext.Relaxed.PreviewCalendarResponse,
                static (http, call) =>
                    QueryReader.Bind(
                        http.Request,
                        static query =>
                            (
                                Version: query.Long("version"),
                                FromUtc: query.DateTime("fromUtc"),
                                Limit: query.PositiveInt("limit"),
                                TimeZoneId: query.Text("timeZoneId")
                            )
                    )
                        is { Version: { } version } preview
                        ? call.Operations.Calendars.PreviewAsync(
                            Key(http),
                            version,
                            preview.FromUtc,
                            preview.Limit ?? 10,
                            Zone(preview.TimeZoneId),
                            call.Ct
                        )
                        : throw new InvalidQueryException("version is required.")
            )
            .WithQueryParameters([
                new QueryParameterDoc("version", QueryParameterKind.Long, "The version to forecast.", Required: true),
                new QueryParameterDoc("fromUtc", QueryParameterKind.Instant, "Forecast from this instant; omit for the database clock."),
                new QueryParameterDoc(
                    "limit",
                    QueryParameterKind.Int,
                    "How many instants or windows to forecast per version, at most 50: a larger value is clamped to 50.",
                    Minimum: 1,
                    Default: 10
                ),
                new QueryParameterDoc(
                    "timeZoneId",
                    QueryParameterKind.String,
                    "The zone a schedule would evaluate the calendar in; omit for UTC."
                ),
            ]);

        controls.MapCommand(
            "/calendars/{jobNamespace}/{calendarName}/drafts",
            new("saveCalendarDraft", ActaPermissions.Admin),
            "Save a calendar definition as the family's next draft version.",
            options,
            ActaApiJsonContext.Relaxed.SaveCalendarDraftRequest,
            "Invalid calendar request.",
            ControlStatus.Calendar<SaveCalendarDraftResponse>(StatusCodes.Status200OK, StatusCodes.Status409Conflict),
            Key,
            static async (call, calendar) =>
            {
                try
                {
                    return await call.Operations.Calendars.SaveDraftAsync(
                        calendar,
                        call.Body!.Role,
                        call.Body.Definition.ValueKind == JsonValueKind.Object
                            ? call.Body.Definition.GetRawText()
                            : throw new InvalidControlException("Invalid calendar request.", "definition must be a JSON object."),
                        call.Body.ExpectedLatestVersion,
                        call.ReasonMessage,
                        call.Actor,
                        call.Ct
                    );
                }
                catch (CalendarDefinitionException ex)
                {
                    // The facade throws for a definition that does not compile; the wire answers it as a rejected draft.
                    return new SaveCalendarDraftResponse(ControlAction.Rejected, null, null, ex.Problems);
                }
            },
            bodyRequired: true
        );
        controls.MapCommand(
            "/calendars/{jobNamespace}/{calendarName}/publish",
            new("publishCalendar", ActaPermissions.Admin),
            "Publish a draft as the family's current version and retime the schedules bound to it.",
            options,
            ActaApiJsonContext.Relaxed.PublishCalendarRequest,
            "Invalid calendar request.",
            ControlStatus.Calendar<PublishCalendarResponse>(
                StatusCodes.Status200OK,
                StatusCodes.Status404NotFound,
                StatusCodes.Status409Conflict
            ),
            Key,
            static (call, calendar) =>
                call.Operations.Calendars.PublishAsync(
                    calendar,
                    call.Body!.CandidateVersion
                        ?? throw new InvalidControlException("Invalid calendar request.", "candidateVersion is required."),
                    call.Body.ExpectedCurrentVersion,
                    call.Body.OverdueDecision,
                    call.ReasonMessage,
                    call.Actor,
                    call.Ct
                ),
            bodyRequired: true
        );
        controls.MapCommand(
            "/calendars/{jobNamespace}/{calendarName}/archive",
            new("archiveCalendar", ActaPermissions.Admin),
            "Archive the family's current version.",
            options,
            ActaApiJsonContext.Relaxed.ArchiveCalendarRequest,
            "Invalid calendar request.",
            ControlStatus.Calendar<ArchiveCalendarResponse>(
                StatusCodes.Status200OK,
                StatusCodes.Status404NotFound,
                StatusCodes.Status409Conflict
            ),
            Key,
            static (call, calendar) =>
                call.Operations.Calendars.ArchiveAsync(
                    calendar,
                    call.Body!.ExpectedCurrentVersion,
                    call.ReasonMessage,
                    call.Actor,
                    call.Ct
                ),
            bodyRequired: true
        );
    }

    /// <summary>
    /// Whether the family exists, which it does exactly when it has a version: a list answers an empty page for an unknown
    /// family, and the route answers 404.
    /// </summary>
    private static async ValueTask<bool> FamilyExistsAsync(ReadCall call, CalendarLookup calendar) =>
        (await call.Operations.Calendars.ListVersionsAsync(new ListCalendarVersionsQuery(calendar, PageSize: 1), call.Ct)).Items.Count > 0;

    /// <summary>The route's family; its namespace may be <c>sys</c>, which holds the shared families, and its calendar name may not.</summary>
    private static CalendarLookup Key(HttpContext http)
    {
        try
        {
            return new(
                NameSyntax.Canonicalize(RouteKeys.Value(http, "jobNamespace"), "jobNamespace"),
                NameSyntax.Canonicalize(
                    RouteKeys.Value(http, "calendarName"),
                    "calendarName",
                    NameSyntax.ExtendedMaxLength,
                    reserveSystem: true
                )
            );
        }
        catch (ArgumentException ex)
        {
            throw new MalformedRouteKeyException(ex.Message);
        }
    }

    /// <summary>The zone id as given; one the runtime does not know is caller input, the 400, where the facade would throw its lookup failure.</summary>
    private static string? Zone(string? timeZoneId) =>
        timeZoneId is null || TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out _)
            ? timeZoneId
            : throw new InvalidQueryException($"timeZoneId '{timeZoneId}' is not a known time zone.");
}
