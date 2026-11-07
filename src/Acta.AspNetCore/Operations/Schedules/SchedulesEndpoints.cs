using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore;

/// <summary>
/// The Schedules routes: the list, a schedule's preview, and pause, resume, and trigger. A schedule is addressed by its natural key, the namespace, job name, and schedule name.
/// </summary>
internal static class SchedulesEndpoints
{
    public static void Map(RouteGroupBuilder reads, RouteGroupBuilder? controls, ActaEndpointOptions options)
    {
        reads.MapList(
            "/schedules",
            new("listSchedules", ActaPermissions.Read),
            "List recurring schedules, next run first.",
            DashboardJsonContext.Default.ListSchedulesResponse,
            static query => new ListSchedulesQuery(
                JobNamespace: query.Text("jobNamespace"),
                JobName: query.Text("jobName"),
                Origin: query.Code("origin", ScheduleOriginCodeExtensions.FromCode),
                LiveOnly: query.Bool("liveOnly") ?? true,
                PageSize: query.Int("pageSize"),
                Cursor: query.Text("cursor"),
                IncludeTotal: query.Bool("includeTotal") ?? false
            ),
            static (call, query) => call.Operations.Schedules.ListAsync(query, call.Ct),
            [
                new QueryParameterDoc("jobNamespace", QueryParameterKind.String, "Only schedules in this namespace."),
                new QueryParameterDoc("jobName", QueryParameterKind.String, "Only schedules on this job name."),
                new QueryParameterDoc(
                    "origin",
                    QueryParameterKind.String,
                    "Exact schedule origin as a kebab code.",
                    CodeKind: "schedule-origin"
                ),
                new QueryParameterDoc(
                    "liveOnly",
                    QueryParameterKind.Bool,
                    "Null or true returns only schedules with no orphaned instant; false includes orphaned ones."
                ),
                .. QueryParameterDocExtensions.PagingCore,
                .. QueryParameterDocExtensions.IncludeTotal,
            ]
        );
        reads
            .MapDetail(
                "/schedules/{jobNamespace}/{jobName}/{scheduleName}/preview",
                new("previewSchedule", ActaPermissions.Read),
                "Preview a schedule's upcoming fire instants.",
                "Schedule",
                DashboardJsonContext.Default.PreviewScheduleResponse,
                static (http, call) =>
                    call.Operations.Schedules.PreviewAsync(
                        Key(http),
                        QueryReader.Bind(http.Request, static query => query.PositiveInt("limit")) ?? 10,
                        call.Ct
                    )
            )
            .WithQueryParameters([
                new QueryParameterDoc(
                    "limit",
                    QueryParameterKind.Int,
                    "How many upcoming occurrences to forecast, at most 50: a larger value is clamped to 50.",
                    Minimum: 1,
                    Default: 10
                ),
            ])
            // A calendar this host cannot resolve answers a problem (RouteTable.Run).
            .ProducesProblem(StatusCodes.Status409Conflict);
        if (controls is null)
        {
            return;
        }

        controls.MapCommand(
            "/schedules/{jobNamespace}/{jobName}/{scheduleName}/pause",
            new("pauseSchedule", ActaPermissions.Control),
            "Pause a recurring schedule, indefinitely or until an instant.",
            options,
            DashboardJsonContext.Default.PauseScheduleRequest,
            "Invalid schedule request.",
            ControlStatus.Schedule<PauseScheduleResponse>(),
            Key,
            static (call, schedule) =>
                call.Operations.Schedules.PauseAsync(schedule, call.Body?.PausedUntilUtc, call.ReasonMessage, call.Actor, call.Ct)
        );
        controls.MapCommand(
            "/schedules/{jobNamespace}/{jobName}/{scheduleName}/resume",
            new("resumeSchedule", ActaPermissions.Control),
            "Resume a paused schedule, reconciled by its misfire policy.",
            options,
            DashboardJsonContext.Default.ResumeScheduleRequest,
            "Invalid schedule request.",
            ControlStatus.Schedule<ResumeScheduleResponse>(),
            Key,
            static (call, schedule) => call.Operations.Schedules.ResumeAsync(schedule, call.ReasonMessage, call.Actor, call.Ct)
        );
        controls.MapCommand(
            "/schedules/{jobNamespace}/{jobName}/{scheduleName}/trigger",
            new("triggerSchedule", ActaPermissions.Control),
            "Fire the schedule now, without moving its cadence.",
            options,
            DashboardJsonContext.Default.TriggerScheduleRequest,
            "Invalid schedule request.",
            ControlStatus.Schedule<TriggerScheduleResponse>(),
            Key,
            static (call, schedule) => call.Operations.Schedules.TriggerAsync(schedule, call.ReasonMessage, call.Actor, call.Ct)
        );
    }

    private static ScheduleLookup Key(HttpContext http)
    {
        try
        {
            return new(
                NameSyntax.Canonicalize(RouteKeys.Value(http, "jobNamespace"), "jobNamespace"),
                NameSyntax.Canonicalize(RouteKeys.Value(http, "jobName"), "jobName"),
                NameSyntax.Canonicalize(RouteKeys.Value(http, "scheduleName"), "scheduleName", NameSyntax.ExtendedMaxLength)
            );
        }
        catch (ArgumentException ex)
        {
            throw new MalformedRouteKeyException(ex.Message);
        }
    }
}
