using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore;

/// <summary>
/// The Jobs routes: one job by its ref or its idempotency key, its tags, its steps, and the control verbs. A verb
/// answers with its response at 200 when applied or already in state, 409 when the job's state or a stale
/// expectedVersion refuses it, and 404 for an unknown ref, so a client reads <c>action</c>, <c>status</c>, and
/// <c>version</c> without special-casing the code.
/// </summary>
internal static class JobsEndpoints
{
    public static void Map(RouteGroupBuilder reads, RouteGroupBuilder controls, ActaEndpointOptions options)
    {
        reads.MapDetail(
            "/jobs/{jobRef}",
            new("getJob", ActaPermissions.Read),
            "Read one job",
            "Job",
            ActaApiJsonContext.Relaxed.GetJobResponse,
            async (http, call) =>
            {
                var job = RouteKeys.Job(http, options);
                return await call.Jobs.GetAsync(job, call.Ct) ?? throw new TargetNotFoundException("Job", job.NotFoundHint());
            }
        );

        // A lookup, not a filter, and deliberately its own route. Both parameters are required and together identify at
        // most one job, so this answers with a single object or 404. Folding it into /jobs would make one route return
        // either a page or a job depending on which query parameters arrived.
        reads
            .MapDetail(
                "/jobs/by-idempotency-key",
                new("getJobByIdempotencyKey", ActaPermissions.Read),
                "Resolve a job by namespace and idempotency key",
                "Job",
                ActaApiJsonContext.Relaxed.GetJobResponse,
                static (http, call) =>
                    QueryReader.Bind(http.Request, static query =>
                        (Namespace: query.Text("jobNamespace"), Key: query.Text("idempotencyKey"))
                    )
                        is { Namespace: { } jobNamespace, Key: { } key }
                        ? call.Jobs.GetAsync(ByIdempotencyKey(jobNamespace, key), call.Ct)
                        : throw new InvalidQueryException("jobNamespace and idempotencyKey are required.")
            )
            .WithQueryParameters([
                new QueryParameterDoc("jobNamespace", QueryParameterKind.String, "The idempotency key's namespace scope.", Required: true),
                new QueryParameterDoc(
                    "idempotencyKey",
                    QueryParameterKind.String,
                    "The caller-supplied idempotency key to resolve.",
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
        reads
            .MapList(
                "/jobs/{jobRef}/checkpoints",
                new("listJobCheckpoints", ActaPermissions.Read),
                "List one job's checkpoints",
                ActaApiJsonContext.Relaxed.ListJobCheckpointsResponse,
                query =>
                    (
                        Job: RouteKeys.Job(query.Http, options),
                        PageSize: query.Int("pageSize"),
                        Cursor: query.Text("cursor"),
                        IncludeTotal: query.Bool("includeTotal") ?? false
                    ),
                static async (call, bound) =>
                    await call.Jobs.ListCheckpointsAsync(
                        new ListJobCheckpointsQuery(
                            JobLookup.ById(
                                await call.Jobs.StoredJobIdAsync(bound.Job, call.Ct)
                                    ?? throw new TargetNotFoundException("Job", bound.Job.NotFoundHint())
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
        GetJobCheckpointValueEndpoint.Map(reads, options);
        reads
            .MapList(
                "/jobs/{jobRef}/steps",
                new("listJobSteps", ActaPermissions.Read),
                "List one job's steps, newest first",
                ActaApiJsonContext.Relaxed.ListJobStepsResponse,
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
                                await call.Jobs.StoredJobIdAsync(bound.Job, call.Ct)
                                    ?? throw new TargetNotFoundException("Job", bound.Job.NotFoundHint())
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
        MapVerbs(controls, options);
        UpdateJobInputEndpoint.Map(controls, options);
        EnqueueJobEndpoint.Map(controls);
        RaiseJobSignalEndpoint.Map(controls);
        ClearJobSignalEndpoint.Map(controls);
    }

    // A verb addresses its job by ref only; the id:<n> debug lookup is for reads.
    private static void MapVerbs(RouteGroupBuilder controls, ActaEndpointOptions options)
    {
        controls.MapCommand(
            "/jobs/{jobRef}/pause",
            new("pauseJob", ActaPermissions.Control),
            "Pause the job until an operator resumes it",
            options,
            ActaApiJsonContext.Relaxed.PauseJobRequest,
            "Invalid request.",
            ControlStatus.Job<PauseJobResponse>(),
            RouteKeys.JobByRef,
            static (call, job) => call.Jobs.PauseAsync(job, call.ReasonMessage, call.Actor, call.Body?.ExpectedVersion, call.Ct)
        );
        controls.MapCommand(
            "/jobs/{jobRef}/resume",
            new("resumeJob", ActaPermissions.Control),
            "Resume a paused job",
            options,
            ActaApiJsonContext.Relaxed.ResumeJobRequest,
            "Invalid request.",
            ControlStatus.Job<ResumeJobResponse>(),
            RouteKeys.JobByRef,
            static (call, job) => call.Jobs.ResumeAsync(job, call.ReasonMessage, call.Actor, call.Body?.ExpectedVersion, call.Ct)
        );
        controls.MapCommand(
            "/jobs/{jobRef}/restart",
            new("restartJob", ActaPermissions.Control),
            "Restart a job that is not executing",
            options,
            ActaApiJsonContext.Relaxed.RestartJobRequest,
            "Invalid request.",
            ControlStatus.Job<RestartJobResponse>(),
            RouteKeys.JobByRef,
            static (call, job) => call.Jobs.RestartAsync(job, call.ReasonMessage, call.Actor, call.Body?.ExpectedVersion, call.Ct)
        );
        controls.MapCommand(
            "/jobs/{jobRef}/cancel",
            new("cancelJob", ActaPermissions.Control),
            "Cancel the job",
            options,
            ActaApiJsonContext.Relaxed.CancelJobRequest,
            "Invalid request.",
            ControlStatus.Job<CancelJobResponse>(),
            RouteKeys.JobByRef,
            static (call, job) => call.Jobs.CancelAsync(job, call.ReasonMessage, call.Actor, call.Body?.ExpectedVersion, call.Ct)
        );
        controls.MapCommand(
            "/jobs/{jobRef}/purge",
            new("purgeJob", ActaPermissions.Admin),
            "Delete the job's record from the ledger",
            options,
            ActaApiJsonContext.Relaxed.PurgeJobRequest,
            "Invalid request.",
            ControlStatus.Job<PurgeJobResponse>(),
            RouteKeys.JobByRef,
            static (call, job) => call.Jobs.PurgeAsync(job, call.ReasonMessage, call.Actor, call.Ct)
        );
        controls.MapCommand(
            "/jobs/{jobRef}/reschedule",
            new("rescheduleJob", ActaPermissions.Control),
            "Move the job's next-run instant, or run it now",
            options,
            ActaApiJsonContext.Relaxed.RescheduleJobRequest,
            "Invalid request.",
            ControlStatus.Job<RescheduleJobResponse>(),
            RouteKeys.JobByRef,
            static (call, job) =>
                call.Jobs.RescheduleAsync(
                    job,
                    call.Body?.NextRunAtUtc is { } instant
                        ? instant > DateTime.MinValue
                            ? ControlEndpointValidation.Instant(instant)
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
            "Change the job's priority",
            options,
            ActaApiJsonContext.Relaxed.ReprioritizeJobRequest,
            "Invalid request.",
            ControlStatus.Job<ReprioritizeJobResponse>(),
            RouteKeys.JobByRef,
            static (call, job) =>
                call.Jobs.ReprioritizeAsync(job, call.Body!.Priority, call.ReasonMessage, call.Actor, call.Body.ExpectedVersion, call.Ct),
            bodyRequired: true
        );
    }

    private static JobLookup ByIdempotencyKey(string jobNamespace, string idempotencyKey)
    {
        try
        {
            return JobLookup.ByIdempotencyKey(jobNamespace, idempotencyKey);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidQueryException(ex.Message);
        }
    }
}
