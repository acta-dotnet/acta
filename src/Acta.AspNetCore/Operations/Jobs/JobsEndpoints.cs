using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore;

/// <summary>
/// The Jobs routes: one job by its ref or its deduplication key, its tags, its steps, and the control verbs. A verb
/// answers with its response at 200 when applied, 409 when the job's state or a stale expectedVersion refuses it, and 404
/// for an unknown ref, so a client reads <c>action</c>, <c>status</c>, and <c>version</c> without special-casing the code.
/// </summary>
internal static class JobsEndpoints
{
    public static void Map(RouteGroupBuilder reads, RouteGroupBuilder? controls, ActaEndpointOptions options)
    {
        reads.MapDetail(
            "/jobs/{jobRef}",
            new("getJob", ActaPermissions.Read),
            "Read one job.",
            "Job",
            ActaApiJsonContext.Default.GetJobResponse,
            (http, call) => call.Jobs.GetAsync(RouteKeys.Job(http, options), call.Ct)
        );

        // A lookup, not a filter, and deliberately its own route. Both parameters are required and together identify at
        // most one job, so this answers with a single object or 404. Folding it into /jobs would make one route return
        // either a page or a job depending on which query parameters arrived.
        reads
            .MapDetail(
                "/jobs/by-deduplication-key",
                new("getJobByDeduplicationKey", ActaPermissions.Read),
                "Resolve a job by namespace and deduplication key.",
                "Job",
                ActaApiJsonContext.Default.GetJobResponse,
                static (http, call) =>
                    QueryReader.Bind(
                        http.Request,
                        static query => (Namespace: query.Text("jobNamespace"), Key: query.Text("deduplicationKey"))
                    )
                        is { Namespace: { } jobNamespace, Key: { } key }
                        ? call.Jobs.GetAsync(ByDeduplicationKey(jobNamespace, key), call.Ct)
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
        reads.MapTags(
            controls,
            "/jobs/{jobRef}/tags",
            "job",
            ActaPermissions.Control,
            http => TagTarget.ForJob(RouteKeys.Job(http, options)),
            async (http, call) => await call.Jobs.StoredJobIdAsync(RouteKeys.Job(http, options), call.Ct) is not null,
            static http => TagTarget.ForJob(RouteKeys.JobByRef(http))
        );
        GetJobInputEndpoint.Map(reads, options);
        GetJobResultEndpoint.Map(reads, options);
        GetJobDetailEndpoint.Map(reads, options);
        ListJobCheckpointsEndpoint.Map(reads, options);
        reads
            .MapList(
                "/jobs/{jobRef}/steps",
                new("listJobSteps", ActaPermissions.Read),
                "List one job's steps, newest first.",
                ActaApiJsonContext.Default.ListJobStepsResponse,
                query =>
                    (
                        Job: RouteKeys.Job(query.Http, options),
                        PageSize: query.Int("pageSize"),
                        Cursor: query.Text("cursor"),
                        IncludeTotal: query.Bool("includeTotal") ?? false
                    ),
                static async (call, bound) =>
                    await call.Jobs.ListStepsAsync(
                        new ListJobStepsQuery(
                            JobLookup.ById(
                                await call.Jobs.StoredJobIdAsync(bound.Job, call.Ct) ?? throw new TargetNotFoundException("Job")
                            ),
                            bound.PageSize,
                            bound.Cursor,
                            bound.IncludeTotal
                        ),
                        call.Ct
                    ),
                [.. QueryParameterDocExtensions.PagingCore, .. QueryParameterDocExtensions.IncludeTotal]
            )
            .ProducesProblem(StatusCodes.Status404NotFound);
        if (controls is not null)
        {
            MapVerbs(controls, options);
            UpdateJobInputEndpoint.Map(controls, options);
            EnqueueJobEndpoint.Map(controls);
            RaiseJobSignalEndpoint.Map(controls);
        }
    }

    // A verb addresses its job by ref only; the id:<n> debug lookup is for reads.
    private static void MapVerbs(RouteGroupBuilder controls, ActaEndpointOptions options)
    {
        controls.MapCommand(
            "/jobs/{jobRef}/pause",
            new("pauseJob", ActaPermissions.Control),
            "Pause the job until an operator resumes it.",
            options,
            ActaApiJsonContext.Default.PauseJobRequest,
            "Invalid request.",
            ControlStatus.Job<PauseJobResponse>(),
            RouteKeys.JobByRef,
            static (call, job) => call.Jobs.PauseAsync(job, call.ReasonMessage, call.Actor, call.Body?.ExpectedVersion, call.Ct)
        );
        controls.MapCommand(
            "/jobs/{jobRef}/resume",
            new("resumeJob", ActaPermissions.Control),
            "Resume a paused job.",
            options,
            ActaApiJsonContext.Default.ResumeJobRequest,
            "Invalid request.",
            ControlStatus.Job<ResumeJobResponse>(),
            RouteKeys.JobByRef,
            static (call, job) => call.Jobs.ResumeAsync(job, call.ReasonMessage, call.Actor, call.Body?.ExpectedVersion, call.Ct)
        );
        controls.MapCommand(
            "/jobs/{jobRef}/restart",
            new("restartJob", ActaPermissions.Control),
            "Restart a terminal job with its original input.",
            options,
            ActaApiJsonContext.Default.RestartJobRequest,
            "Invalid request.",
            ControlStatus.Job<RestartJobResponse>(),
            RouteKeys.JobByRef,
            static (call, job) => call.Jobs.RestartAsync(job, call.ReasonMessage, call.Actor, call.Body?.ExpectedVersion, call.Ct)
        );
        controls.MapCommand(
            "/jobs/{jobRef}/cancel",
            new("cancelJob", ActaPermissions.Control),
            "Cancel the job.",
            options,
            ActaApiJsonContext.Default.CancelJobRequest,
            "Invalid request.",
            ControlStatus.Job<CancelJobResponse>(),
            RouteKeys.JobByRef,
            static (call, job) => call.Jobs.CancelAsync(job, call.ReasonMessage, call.Actor, call.Body?.ExpectedVersion, call.Ct)
        );
        controls.MapCommand(
            "/jobs/{jobRef}/purge",
            new("purgeJob", ActaPermissions.Admin),
            "Delete the job's record from the ledger.",
            options,
            ActaApiJsonContext.Default.PurgeJobRequest,
            "Invalid request.",
            ControlStatus.Job<PurgeJobResponse>(),
            RouteKeys.JobByRef,
            static (call, job) => call.Jobs.PurgeAsync(job, call.ReasonMessage, call.Actor, call.Ct)
        );
        controls.MapCommand(
            "/jobs/{jobRef}/reschedule",
            new("rescheduleJob", ActaPermissions.Control),
            "Move the job's next-run instant, or run it now.",
            options,
            ActaApiJsonContext.Default.RescheduleJobRequest,
            "Invalid request.",
            ControlStatus.Job<RescheduleJobResponse>(),
            RouteKeys.JobByRef,
            static (call, job) =>
                call.Jobs.RescheduleAsync(
                    job,
                    call.Body?.NextRunAtUtc is { } instant
                        ? instant > DateTime.MinValue
                            ? instant
                            : throw new InvalidControlException("Invalid request.", "nextRunAtUtc must be an instant, or omitted for now.")
                        : null,
                    call.ReasonMessage,
                    call.Actor,
                    call.Body?.ExpectedVersion,
                    call.Ct
                ),
            bodyRequired: false
        );
        controls.MapCommand(
            "/jobs/{jobRef}/reprioritize",
            new("reprioritizeJob", ActaPermissions.Control),
            "Change the job's priority.",
            options,
            ActaApiJsonContext.Default.ReprioritizeJobRequest,
            "Invalid request.",
            ControlStatus.Job<ReprioritizeJobResponse>(),
            RouteKeys.JobByRef,
            static (call, job) =>
                call.Jobs.ReprioritizeAsync(job, call.Body!.Priority, call.ReasonMessage, call.Actor, call.Body.ExpectedVersion, call.Ct),
            bodyRequired: true
        );
    }

    private static JobLookup ByDeduplicationKey(string jobNamespace, string deduplicationKey)
    {
        try
        {
            return JobLookup.ByDeduplicationKey(jobNamespace, deduplicationKey);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidQueryException(ex.Message);
        }
    }
}
