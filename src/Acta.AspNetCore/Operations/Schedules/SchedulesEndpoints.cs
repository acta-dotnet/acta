using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore;

/// <summary>
/// Optional body of a schedule pause POST (the schedule is addressed by the route's natural key).
/// <c>PausedUntilUtc</c> null is an indefinite pause, a timestamp is a timed pause. The framework
/// stamps the actor itself, so the body carries only the pause window and the operator note.
/// Resume and trigger carry only a <c>reasonMessage</c> and share the generic control-request shape.
/// </summary>
internal sealed record PauseScheduleHttpRequest(DateTime? PausedUntilUtc = null, string? ReasonMessage = null) : IControlRequest;

/// <summary>
/// Body of a schedule overrides POST (the schedule is addressed by the route's natural key).
/// <c>ExpectedVersion</c> is the CAS token (the schedule's current <c>version</c>). Full-set
/// semantics: a null <c>Expression</c>/<c>TimeZoneId</c> clears that override.
/// </summary>
internal sealed record SetScheduleOverridesHttpRequest(
    int? ExpectedVersion = null,
    string? Expression = null,
    string? TimeZoneId = null,
    string? ReasonMessage = null
) : IControlRequest;

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
            DashboardJsonContext.Default.PagedResultScheduleListItem,
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
                DashboardJsonContext.Default.SchedulePreview,
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
            DashboardJsonContext.Default.PauseScheduleHttpRequest,
            "Invalid schedule request.",
            ControlStatus.Schedule<PauseScheduleResponse>(),
            Key,
            static (call, schedule) =>
                call.Operations.Schedules.PauseAsync(schedule, call.Body?.PausedUntilUtc, call.ReasonMessage, call.ActorKey, call.Ct)
        );
        controls.MapCommand(
            "/schedules/{jobNamespace}/{jobName}/{scheduleName}/resume",
            "Resume a paused schedule, reconciled by its misfire policy.",
            options,
            DashboardJsonContext.Default.ControlHttpRequest,
            "Invalid schedule request.",
            ControlStatus.Schedule<ResumeScheduleResponse>(),
            Key,
            static (call, schedule) => call.Operations.Schedules.ResumeAsync(schedule, call.ReasonMessage, call.ActorKey, call.Ct)
        );
        controls.MapCommand(
            "/schedules/{jobNamespace}/{jobName}/{scheduleName}/trigger",
            "Fire the schedule now, without moving its cadence.",
            options,
            DashboardJsonContext.Default.ControlHttpRequest,
            "Invalid schedule request.",
            ControlStatus.Schedule<TriggerScheduleNowResponse>(),
            Key,
            static (call, schedule) => call.Operations.Schedules.TriggerNowAsync(schedule, call.ReasonMessage, call.ActorKey, call.Ct)
        );
        controls.MapCommand(
            "/schedules/{jobNamespace}/{jobName}/{scheduleName}/overrides",
            "Set or clear the schedule's expression and time-zone overrides.",
            options,
            DashboardJsonContext.Default.SetScheduleOverridesHttpRequest,
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
                    call.ActorKey,
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
