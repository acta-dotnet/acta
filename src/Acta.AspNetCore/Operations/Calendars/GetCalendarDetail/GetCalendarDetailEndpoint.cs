using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore;

/// <summary>
/// GET /calendars/{jobNamespace}/{calendarName}/detail: the calendar screen in one request, composed by
/// <see cref="GetCalendarDetailResponse"/>.
/// </summary>
internal static class GetCalendarDetailEndpoint
{
    public static void Map(RouteGroupBuilder reads, Func<HttpContext, CalendarLookup> key) =>
        reads
            .MapDetail(
                "/calendars/{jobNamespace}/{calendarName}/detail",
                "Read the calendar screen: the head, and the first page of its versions and of the schedules naming it.",
                "Calendar",
                DashboardJsonContext.Default.GetCalendarDetailResponse,
                (http, call) =>
                    GetCalendarDetailResponse.ComposeAsync(
                        call.Operations.Calendars,
                        key(http),
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
}
