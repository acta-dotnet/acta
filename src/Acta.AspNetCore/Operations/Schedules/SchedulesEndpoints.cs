using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore;

/// <summary>
/// The Schedules routes: the list, one schedule, a schedule's preview, and pause, resume, and trigger. A schedule is addressed by its natural key, the namespace, job name, and schedule name.
/// </summary>
internal static class SchedulesEndpoints
{
    public static void Map(RouteGroupBuilder reads, RouteGroupBuilder controls, ActaEndpointOptions options)
    {
        reads.MapList(
            "/schedules",
            new("listSchedules", ActaPermissions.Read),
            "List recurring schedules; the live ones come next run first.",
            ActaApiJsonContext.Relaxed.ListSchedulesResponse,
            static query => new ListSchedulesQuery(
                JobNamespace: query.Text("jobNamespace"),
                JobName: query.Text("jobName"),
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
                    "liveOnly",
                    QueryParameterKind.Bool,
                    "Left out or true, only live schedules, next run first; false also lists orphaned ones, oldest first."
                ),
                .. QueryParameterDocExtensions.PagingCore,
                .. QueryParameterDocExtensions.IncludeTotal,
            ]
        );
        reads.MapDetail(
            "/schedules/{jobNamespace}/{jobName}/{scheduleName}",
            new("getSchedule", ActaPermissions.Read),
            "Read one schedule.",
            "Schedule",
            ActaApiJsonContext.Relaxed.GetScheduleResponse,
            static (http, call) => call.Operations.Schedules.GetAsync(Key(http), call.Ct)
        );
        reads
            .MapDetail(
                "/schedules/{jobNamespace}/{jobName}/{scheduleName}/preview",
                new("previewSchedule", ActaPermissions.Read),
                "Preview a schedule's upcoming fire instants.",
                "Schedule",
                ActaApiJsonContext.Relaxed.PreviewScheduleResponse,
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
            // A calendar this host cannot resolve, and a source that cannot be evaluated, each answer a problem (RouteTable.Run).
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        controls.MapCommand(
            "/schedules/{jobNamespace}/{jobName}/{scheduleName}/pause",
            new("pauseSchedule", ActaPermissions.Control),
            "Pause a recurring schedule, indefinitely or until an instant.",
            options,
            ActaApiJsonContext.Relaxed.PauseScheduleRequest,
            "Invalid schedule request.",
            ControlStatus.Schedule<PauseScheduleResponse>(
                StatusCodes.Status200OK,
                StatusCodes.Status404NotFound,
                StatusCodes.Status409Conflict
            ),
            Key,
            static (call, schedule) =>
                call.Operations.Schedules.PauseAsync(schedule, call.Body?.PausedUntilUtc, call.ReasonMessage, call.Actor, call.Ct)
        );
        controls.MapCommand(
            "/schedules/{jobNamespace}/{jobName}/{scheduleName}/resume",
            new("resumeSchedule", ActaPermissions.Control),
            "Resume a paused schedule, reconciled by its misfire policy.",
            options,
            ActaApiJsonContext.Relaxed.ResumeScheduleRequest,
            "Invalid schedule request.",
            ControlStatus.Schedule<ResumeScheduleResponse>(StatusCodes.Status200OK, StatusCodes.Status404NotFound),
            Key,
            static (call, schedule) => call.Operations.Schedules.ResumeAsync(schedule, call.ReasonMessage, call.Actor, call.Ct)
        );
        controls.MapCommand(
            "/schedules/{jobNamespace}/{jobName}/{scheduleName}/trigger",
            new("triggerSchedule", ActaPermissions.Control),
            "Fire the schedule now, without moving its cadence.",
            options,
            ActaApiJsonContext.Relaxed.TriggerScheduleRequest,
            "Invalid schedule request.",
            ControlStatus.Schedule<TriggerScheduleResponse>(
                StatusCodes.Status200OK,
                StatusCodes.Status404NotFound,
                StatusCodes.Status409Conflict
            ),
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
                NameSyntax.Canonicalize(RouteKeys.Value(http, "jobName"), "jobName", NameSyntax.ExtendedMaxLength),
                NameSyntax.Canonicalize(RouteKeys.Value(http, "scheduleName"), "scheduleName", NameSyntax.ExtendedMaxLength)
            );
        }
        catch (ArgumentException ex)
        {
            throw new MalformedRouteKeyException(ex.Message);
        }
    }
}
