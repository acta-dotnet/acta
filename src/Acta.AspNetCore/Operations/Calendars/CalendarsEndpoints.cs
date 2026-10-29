using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore;

/// <summary>
/// The Calendars routes: the catalog list, one family, a version's preview, and save draft, publish, and archive. A family
/// is addressed by its namespace and calendar name; the namespace <c>sys</c> holds the shared families.
/// </summary>
internal static class CalendarsEndpoints
{
    public static void Map(RouteGroupBuilder reads, RouteGroupBuilder? controls, ActaEndpointOptions options)
    {
        reads.MapList(
            "/calendars",
            "List calendar families, the shared ones in sys included.",
            DashboardJsonContext.Default.ListCalendarsResponse,
            static query => new ListCalendarsQuery(
                JobNamespace: query.Text("jobNamespace"),
                Role: query.Enum<CalendarRoleCode>("role"),
                NameSearch: query.Text("nameSearch"),
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
                    "nameSearch",
                    QueryParameterKind.String,
                    "A dotted kebab fragment matched anywhere in the calendar name."
                ),
                .. QueryParameterDocExtensions.PagingCore,
                .. QueryParameterDocExtensions.IncludeTotal,
            ]
        );
        reads
            .MapDetail(
                "/calendars/{jobNamespace}/{calendarName}",
                "Read a calendar family: its versions, one definition, and the schedules bound to it.",
                "Calendar",
                DashboardJsonContext.Default.GetCalendarResponse,
                static (http, call) =>
                    call.Operations.Calendars.GetAsync(
                        Key(http),
                        QueryReader.Bind(http.Request, static query => query.Int("version")),
                        call.Ct
                    )
            )
            .WithQueryParameters([
                new QueryParameterDoc(
                    "version",
                    QueryParameterKind.Int,
                    "The version whose definition to show; omit for the current version, else the latest."
                ),
            ]);
        reads
            .MapDetail(
                "/calendars/{jobNamespace}/{calendarName}/preview",
                "Preview a calendar version beside the current one.",
                "Calendar",
                DashboardJsonContext.Default.PreviewCalendarResponse,
                static (http, call) =>
                    QueryReader.Bind(
                        http.Request,
                        static query =>
                            (
                                Version: query.Int("version"),
                                FromUtc: query.DateTime("fromUtc"),
                                Limit: query.Int("limit"),
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
                new QueryParameterDoc("version", QueryParameterKind.Int, "The version to forecast.", Required: true),
                new QueryParameterDoc("fromUtc", QueryParameterKind.Instant, "Forecast from this instant; omit for the database clock."),
                new QueryParameterDoc("limit", QueryParameterKind.Int, "How many instants or windows to forecast per version."),
                new QueryParameterDoc(
                    "timeZoneId",
                    QueryParameterKind.String,
                    "The zone a schedule would evaluate the calendar in; omit for UTC."
                ),
            ]);
        if (controls is null)
        {
            return;
        }

        controls.MapCommand(
            "/calendars/{jobNamespace}/{calendarName}/drafts",
            "Save a calendar definition as the family's next draft version.",
            options,
            DashboardJsonContext.Default.SaveCalendarDraftRequest,
            "Invalid calendar request.",
            ControlStatus.Calendar<SaveCalendarDraftResponse>(),
            Key,
            static (call, calendar) =>
                call.Operations.Calendars.SaveDraftAsync(
                    calendar,
                    call.Body!.Role,
                    call.Body.Definition.ValueKind == JsonValueKind.Object
                        ? call.Body.Definition.GetRawText()
                        : throw new InvalidControlException("Invalid calendar request.", "definition must be a JSON object."),
                    call.Body.ExpectedLatestVersion,
                    call.ReasonMessage,
                    call.Actor,
                    call.Ct
                ),
            bodyRequired: true
        );
        controls.MapCommand(
            "/calendars/{jobNamespace}/{calendarName}/publish",
            "Publish a draft as the family's current version and retime the schedules bound to it.",
            options,
            DashboardJsonContext.Default.PublishCalendarRequest,
            "Invalid calendar request.",
            ControlStatus.Calendar<PublishCalendarResponse>(),
            Key,
            static (call, calendar) =>
                call.Operations.Calendars.PublishAsync(
                    calendar,
                    call.Body!.ExpectedCurrentVersion,
                    call.Body.CandidateVersion,
                    call.Body.OverdueDecision,
                    call.ReasonMessage,
                    call.Actor,
                    call.Ct
                ),
            bodyRequired: true
        );
        controls.MapCommand(
            "/calendars/{jobNamespace}/{calendarName}/archive",
            "Archive the family's current version.",
            options,
            DashboardJsonContext.Default.ArchiveCalendarRequest,
            "Invalid calendar request.",
            ControlStatus.Calendar<ArchiveCalendarResponse>(),
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
