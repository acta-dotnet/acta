using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore;

/// <summary>
/// Body of the job-control POSTs that accept a compare-and-set token (pause, resume, restart, cancel).
/// A null <c>ExpectedVersion</c> applies the verb unconditionally; a non-null one that misses the row's
/// current version answers 409 with action versionConflict and writes nothing. Purge and the admin
/// suspend/resume verbs take no token and keep the reason-only <see cref="JobControlRequest"/>.
/// </summary>
internal sealed record JobVersionedControlRequest(string? ReasonMessage = null, int? ExpectedVersion = null) : IControlRequest;

/// <summary>
/// Body of a job-reschedule POST. <c>NextRunAtUtc</c> is mandatory (missing or default is a 400); the
/// framework stamps the actor and reason code itself. <c>ExpectedVersion</c> is the optional CAS guard.
/// </summary>
internal sealed record JobRescheduleRequest(DateTime NextRunAtUtc = default, string? ReasonMessage = null, int? ExpectedVersion = null)
    : IControlRequest;

/// <summary>
/// Body of a job-reprioritize POST. <c>Priority</c> is mandatory, since its default is the lowest band;
/// a missing or unrecognized one fails deserialization (400). The framework stamps the actor and reason
/// code itself. <c>ExpectedVersion</c> is the optional CAS guard.
/// </summary>
internal sealed record JobReprioritizeRequest(
    [property: JsonRequired] JobPriorityCode Priority,
    string? ReasonMessage = null,
    int? ExpectedVersion = null
) : IControlRequest;

/// <summary>
/// The Jobs routes: one job by its ref or its deduplication key, its tags, and the control verbs. A verb answers with
/// its response at 200 when applied, 409 when the job's state or a stale expectedVersion refuses it, and 404 for an
/// unknown ref, so a client reads <c>action</c>, <c>status</c>, and <c>version</c> without special-casing the code.
/// </summary>
internal static class JobsEndpoints
{
    public static void Map(RouteGroupBuilder reads, RouteGroupBuilder? controls, ActaEndpointOptions options)
    {
        reads.MapDetail(
            "/jobs/{jobRef}",
            "Read one job.",
            "Job",
            DashboardJsonContext.Default.JobDetail,
            (http, call) => call.Jobs.GetAsync(RouteKeys.Job(http, options), call.Ct)
        );

        // A lookup, not a filter, and deliberately its own route. Both parameters are required and together identify at
        // most one job, so this answers with a single object or 404. Folding it into /jobs would make one route return
        // either a page or a job depending on which query parameters arrived.
        reads
            .MapDetail(
                "/jobs/by-key",
                "Resolve a job by namespace and deduplication key.",
                "Job",
                DashboardJsonContext.Default.JobDetail,
                static (http, call) =>
                    QueryReader.Bind(
                        http.Request,
                        static query => (Namespace: query.Text("jobNamespace"), Key: query.Text("deduplicationKey"))
                    )
                        is { Namespace: { } jobNamespace, Key: { } key }
                        ? call.Jobs.GetAsync(JobLookup.ByDeduplicationKey(jobNamespace, key), call.Ct)
                        : throw new InvalidQueryException("jobNamespace and deduplicationKey are required.")
            )
            .WithQueryParameters([
                new QueryParameterDoc(
                    "jobNamespace",
                    QueryParameterKind.String,
                    "The deduplication key's namespace scope.",
                    Required: true
                ),
                new QueryParameterDoc(
                    "deduplicationKey",
                    QueryParameterKind.String,
                    "The caller-supplied deduplication key to resolve.",
                    Required: true
                ),
            ]);
        reads.MapTags(controls, "/jobs/{jobRef}/tags", "job", options, http => TagTarget.ForJob(RouteKeys.Job(http, options)));
        if (controls is not null)
        {
            MapVerbs(controls, options);
        }
    }

    // A verb addresses its job by ref only; the id:<n> debug lookup is for reads.
    private static void MapVerbs(RouteGroupBuilder controls, ActaEndpointOptions options)
    {
        controls.MapCommand(
            "/jobs/{jobRef}/pause",
            "Pause the job until an operator resumes it.",
            options,
            DashboardJsonContext.Default.JobVersionedControlRequest,
            "Invalid request.",
            ControlStatus.Job<PauseJobResponse>(),
            RouteKeys.JobByRef,
            static (call, job) => call.Jobs.PauseAsync(job, call.ReasonMessage, call.ActorKey, call.Body?.ExpectedVersion, call.Ct)
        );
        controls.MapCommand(
            "/jobs/{jobRef}/resume",
            "Resume a paused job.",
            options,
            DashboardJsonContext.Default.JobVersionedControlRequest,
            "Invalid request.",
            ControlStatus.Job<ResumeJobResponse>(),
            RouteKeys.JobByRef,
            static (call, job) => call.Jobs.ResumeAsync(job, call.ReasonMessage, call.ActorKey, call.Body?.ExpectedVersion, call.Ct)
        );
        controls.MapCommand(
            "/jobs/{jobRef}/restart",
            "Restart a terminal job with its original input.",
            options,
            DashboardJsonContext.Default.JobVersionedControlRequest,
            "Invalid request.",
            ControlStatus.Job<RestartJobResponse>(),
            RouteKeys.JobByRef,
            static (call, job) => call.Jobs.RestartAsync(job, call.ReasonMessage, call.ActorKey, call.Body?.ExpectedVersion, call.Ct)
        );
        controls.MapCommand(
            "/jobs/{jobRef}/cancel",
            "Cancel the job.",
            options,
            DashboardJsonContext.Default.JobVersionedControlRequest,
            "Invalid request.",
            ControlStatus.Job<CancelJobResponse>(),
            RouteKeys.JobByRef,
            static (call, job) => call.Jobs.CancelAsync(job, call.ReasonMessage, call.ActorKey, call.Body?.ExpectedVersion, call.Ct)
        );

        // Purge takes no reason and no version token: its body passes the same confirmation and shape checks as the
        // other verbs, but the reason is never forwarded.
        controls.MapCommand(
            "/jobs/{jobRef}/purge",
            "Delete the job's record from the ledger.",
            options,
            DashboardJsonContext.Default.JobControlRequest,
            "Invalid request.",
            ControlStatus.Job<PurgeJobResponse>(),
            RouteKeys.JobByRef,
            static (call, job) => call.Jobs.PurgeAsync(job, call.ActorKey, call.Ct)
        );
        controls.MapCommand(
            "/jobs/{jobRef}/reschedule",
            "Move the job's next-run instant.",
            options,
            DashboardJsonContext.Default.JobRescheduleRequest,
            "Invalid request.",
            ControlStatus.Job<RescheduleJobResponse>(),
            RouteKeys.JobByRef,
            static (call, job) =>
                call.Jobs.RescheduleAsync(
                    job,
                    call.Body!.NextRunAtUtc > DateTime.MinValue
                        ? call.Body.NextRunAtUtc
                        : throw new InvalidControlException("Invalid request.", "nextRunAtUtc is required."),
                    call.ReasonMessage,
                    call.ActorKey,
                    call.Body.ExpectedVersion,
                    call.Ct
                ),
            bodyRequired: true
        );
        controls.MapCommand(
            "/jobs/{jobRef}/reprioritize",
            "Change the job's priority.",
            options,
            DashboardJsonContext.Default.JobReprioritizeRequest,
            "Invalid request.",
            ControlStatus.Job<ReprioritizeJobResponse>(),
            RouteKeys.JobByRef,
            static (call, job) =>
                call.Jobs.ReprioritizeAsync(
                    job,
                    call.Body!.Priority,
                    call.ReasonMessage,
                    call.ActorKey,
                    call.Body.ExpectedVersion,
                    call.Ct
                ),
            bodyRequired: true
        );
    }
}
