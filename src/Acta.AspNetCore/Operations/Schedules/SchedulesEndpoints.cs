using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore;

/// <summary>
/// The Schedules routes: the list, a schedule's preview and tags, and pause, resume, trigger, and the overrides
/// update. A schedule is addressed by its natural key, the namespace, job name, and schedule name.
/// </summary>
internal static class SchedulesEndpoints
{
    public static void Map(RouteGroupBuilder reads, RouteGroupBuilder? controls, ActaEndpointOptions options)
    {
        reads.MapList(
            "/schedules",
            "List recurring schedules, next run first.",
            DashboardJsonContext.Default.ListSchedulesResponse,
            static query => new ListSchedulesQuery(
                JobNamespace: query.Text("jobNamespace"),
                JobName: query.Text("jobName"),
                Origin: query.Enum<ScheduleOriginCode>("origin"),
                LiveOnly: query.Bool("liveOnly") ?? true,
                PageSize: query.Int("pageSize"),
                Cursor: query.Text("cursor"),
                IncludeTotal: query.Bool("includeTotal") ?? false,
                Tags: query.Tags()
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
                .. QueryParameterDocExtensions.TagFilter,
            ]
        );
        reads
            .MapDetail(
                "/schedules/{jobNamespace}/{jobName}/{scheduleName}/preview",
                "Preview a schedule's upcoming fire instants.",
                "Schedule",
                DashboardJsonContext.Default.PreviewScheduleResponse,
                static (http, call) =>
                    call.Operations.Schedules.PreviewAsync(
                        Key(http),
                        QueryReader.Bind(http.Request, static query => query.Int("limit")) ?? 10,
                        call.Ct
                    )
            )
            .WithQueryParameters([new QueryParameterDoc("limit", QueryParameterKind.Int, "How many upcoming occurrences to forecast.")]);
        reads.MapTags(
            controls,
            "/schedules/{jobNamespace}/{jobName}/{scheduleName}/tags",
            "schedule",
            options,
            DashboardJsonContext.Default.UpsertScheduleTagsRequest,
            static http => TagTarget.ForSchedule(Key(http))
        );
        if (controls is null)
        {
            return;
        }

        controls.MapCommand(
            "/schedules/{jobNamespace}/{jobName}/{scheduleName}/pause",
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
            "Fire the schedule now, without moving its cadence.",
            options,
            DashboardJsonContext.Default.TriggerScheduleNowRequest,
            "Invalid schedule request.",
            ControlStatus.Schedule<TriggerScheduleNowResponse>(),
            Key,
            static (call, schedule) => call.Operations.Schedules.TriggerNowAsync(schedule, call.ReasonMessage, call.Actor, call.Ct)
        );
        controls.MapCommand(
            "/schedules/{jobNamespace}/{jobName}/{scheduleName}/overrides",
            "Set or clear the schedule's expression and time-zone overrides.",
            options,
            DashboardJsonContext.Default.SetScheduleOverridesRequest,
            "Invalid schedule request.",
            ControlStatus.Schedule<SetScheduleOverridesResponse>(),
            Key,
            static (call, schedule) =>
                call.Operations.Schedules.UpdateOverridesAsync(
                    schedule,
                    call.Body!.ExpectedVersion ?? throw new InvalidControlException("Invalid request.", "expectedVersion is required."),
                    call.Body.Expression,
                    call.Body.TimeZoneId,
                    call.ReasonMessage,
                    call.Actor,
                    call.Ct
                ),
            bodyRequired: true
        );
    }

    private static ScheduleLookup Key(HttpContext http) =>
        new(
            JobLookup.ByDeduplicationKey(RouteKeys.Value(http, "jobNamespace"), RouteKeys.Value(http, "jobName")),
            RouteKeys.Value(http, "scheduleName")
        );
}
