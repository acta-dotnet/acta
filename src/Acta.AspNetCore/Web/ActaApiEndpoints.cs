using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acta.AspNetCore.Web;

/// <summary>
/// Minimal-API endpoints over <see cref="IJobs"/> and <see cref="IActaOperations"/>: GET list and
/// detail reads plus, when enabled, the POST job-control verbs. Query values bind explicitly so
/// malformed input maps to 400, an invalid cursor maps to 400, and a missing job maps to 404.
/// Responses are never cached.
/// </summary>
internal static class ActaApiEndpoints
{
    public static void Map(RouteGroupBuilder group, ActaEndpointOptions options)
    {
        // Names and tags for every endpoint below, derived from its own route. Applied at the group so
        // it reaches the nested controls group too.
        ActaEndpointIdentity.Apply(group);

        // The error model, declared once. Every endpoint under this group can answer these, because
        // the filters below produce them: 400 from the per-endpoint input guards, 413 from the body
        // ceiling, 500 and 503 from the exception backstop. Declaring them at the group is what puts
        // them in the committed contract without restating four lines on 63 endpoints.
        group.ProducesProblem(StatusCodes.Status400BadRequest);
        group.ProducesProblem(StatusCodes.Status413PayloadTooLarge);
        group.ProducesProblem(StatusCodes.Status500InternalServerError);
        group.ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // Responses are declared; request bodies are not, and deliberately. Accepts<T> writes an
        // accepted-content-type onto the endpoint, and routing then refuses a request that does not
        // carry it - which turned every optional body here into a 404, because these endpoints read
        // their bodies from HttpContext and treat them as optional. Six endpoint tests caught it.
        // Documenting a schema is not worth a routing regression, so the request shapes live in the
        // per-endpoint comments and in docs/ until the framework offers schema-only metadata.

        // Never surface a raw 500/stack from the dashboard API: any unhandled exception (e.g. the database
        // is unreachable) becomes a 503 ProblemDetails the frontend shows as a banner and retries. Known
        // input errors are still mapped to 400/404 by the per-endpoint Guard before they reach this backstop.
        group.AddEndpointFilter(
            static async (context, next) =>
            {
                try
                {
                    return await next(context);
                }
                catch (OperationCanceledException) when (context.HttpContext.RequestAborted.IsCancellationRequested)
                {
                    // Client disconnected or the host is shutting down mid-request: not a fault, no log,
                    // and do not rethrow into developer exception middleware. A connected test transport
                    // can observe 499; a genuinely disconnected client has no response channel left.
                    return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
                }
                catch (EnqueueRejectedException ex)
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status409Conflict,
                        title: "Enqueue rejected.",
                        detail: ex.Message,
                        extensions: new Dictionary<string, object?> { ["reasonCode"] = ex.Reason.ToString() }
                    );
                }
                catch (PayloadTooLargeException ex)
                {
                    // The BoundedReadStream ceiling tripped mid-read (a chunked body that outran its
                    // declared length). Same type the ledger throws for an oversized payload.
                    return Results.Problem(
                        statusCode: StatusCodes.Status413PayloadTooLarge,
                        title: "Request body too large.",
                        detail: ex.Message
                    );
                }
                catch (BadHttpRequestException ex)
                {
                    // The server itself rejected the request (malformed framing, bad chunked encoding).
                    return Results.Problem(statusCode: ex.StatusCode, title: "Invalid request.", detail: ex.Message);
                }
                catch (Exception ex)
                {
                    var loggerFactory = context.HttpContext.RequestServices.GetService(typeof(ILoggerFactory)) as ILoggerFactory;
                    loggerFactory?.CreateLogger("Acta.AspNetCore.Web").LogError(ex, "Unhandled Acta API exception.");

                    // Classify without echoing exception text: driver messages can carry connection
                    // strings. A fixed reason keyed off the exception family stays safe and still tells
                    // a local developer what actually broke; the full detail is in the host log above.
                    // Only the known-transient family (database/network/timeout, including a provider
                    // command timeout surfacing as a non-abort cancellation) reports 503 retry-later;
                    // anything else is a server fault and reports 500 so it is never mistaken for a
                    // recoverable outage.
                    var transientFailure = false;
                    for (var cause = ex; cause is not null; cause = cause.InnerException)
                    {
                        if (
                            cause
                            is System.Data.Common.DbException
                                or System.Net.Sockets.SocketException
                                or TimeoutException
                                or OperationCanceledException
                        )
                        {
                            transientFailure = true;
                            break;
                        }
                    }

                    return transientFailure
                        ? Results.Problem(
                            statusCode: StatusCodes.Status503ServiceUnavailable,
                            title: "Service unavailable.",
                            detail: "The Acta API is temporarily unavailable: the database is unreachable or rejected the request."
                        )
                        : Results.Problem(
                            statusCode: StatusCodes.Status500InternalServerError,
                            title: "Internal server error.",
                            detail: "The Acta API failed to process the request; see the host log for detail."
                        );
                }
            }
        );

        group.AddEndpointFilter(
            static async (context, next) =>
            {
                context.HttpContext.Response.Headers.CacheControl = "no-store";
                return await next(context);
            }
        );

        // Aggregate request-body ceiling for every mapped endpoint. Declared lengths reject up front;
        // the bounded stream keeps the ceiling enforced for chunked bodies, surfacing an overrun as the
        // 413 BadHttpRequestException the error backstop above translates.
        group.AddEndpointFilter(
            async (context, next) =>
            {
                var request = context.HttpContext.Request;

                // The one payload ceiling, read from the runtime options. A body the ledger would
                // refuse is refused here, with the same number rather than a second one.
                var cap = context.HttpContext.RequestServices.GetRequiredService<IOptions<JobsOptions>>().Value.MaxInlinePayloadBytes;

                if (request.ContentLength is { } declared && declared > cap)
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status413PayloadTooLarge,
                        title: "Request body too large.",
                        detail: $"Request bodies on this endpoint are capped at {cap} bytes."
                    );
                }

                request.Body = new BoundedReadStream(request.Body, cap);
                return await next(context);
            }
        );

        if (options.EnableControls)
        {
            // A nested group (empty prefix, same routes) rather than mapping straight onto `group`: it
            // gives the authorization filter below a scope that covers every control endpoint and no
            // read endpoint, without touching the endpoint families' own Map methods.
            var controls = group.MapGroup("");
            ControlAuthorizationFilter.Attach(controls);

            // Controls add one code the reads cannot answer: 403, when the host's own authorizer
            // denies the request. The per-family 404 and 409 are declared where their bodies are.
            controls.ProducesProblem(StatusCodes.Status403Forbidden);
            ActaControlEndpoints.Map(controls, options);
            Features.Jobs.JobDepthEndpoints.MapControls(controls, options);
            ScheduleControlEndpoints.Map(controls, options);
            DefinitionControlEndpoints.Map(controls, options);
            TenantControlEndpoints.Map(controls, options);
            NamespaceControlEndpoints.Map(controls, options);
            AlertControlEndpoints.Map(controls, options);
            Features.Tags.TagEndpoints.MapControls(controls, options);
        }

        // The depth payload reads (input/result/checkpoints/input-template) are part of the always-on
        // read surface: Acta operators see everything, so they map unconditionally alongside the other
        // reads. A size cap inside the endpoints is the only payload-read guard; mutations gate above.
        Features.Jobs.JobDepthEndpoints.MapReads(group, options);
        Features.Tags.TagEndpoints.MapReads(group, options);

        group
            .MapGet(
                "/jobs",
                (HttpContext http, IJobs jobs, IActaOperations operations, CancellationToken ct) =>
                {
                    string? error = null;
                    if (
                        !QueryBinding.TryEnum<JobStatusCode>(http.Request.Query, "status", out var status, ref error)
                        || !QueryBinding.TryInt(http.Request.Query, "tenantId", out var tenantId, ref error)
                        || !QueryBinding.TryInt(http.Request.Query, "pageSize", out var pageSize, ref error)
                        || !QueryBinding.TryBool(http.Request.Query, "includeTotal", out var includeTotal, ref error)
                        || !QueryBinding.TryBool(http.Request.Query, "terminalOnly", out var terminalOnly, ref error)
                        || !QueryBinding.TryBool(http.Request.Query, "recurringOnly", out var recurringOnly, ref error)
                    )
                    {
                        return Task.FromResult(BadRequest(error));
                    }

                    var parentRefText = QueryBinding.Text(http.Request.Query, "parentRef");
                    JobLookup? parentFilter = null;
                    if (parentRefText is not null)
                    {
                        if (!JobTargetBinding.TryParseTarget(parentRefText, options, out var parsed))
                        {
                            return Task.FromResult(BadRequest("parentRef is not a valid job ref."));
                        }

                        parentFilter = parsed;
                    }

                    return Guard(async () =>
                    {
                        long? parentJobId = null;
                        if (parentFilter is { } filter)
                        {
                            parentJobId = await jobs.ResolveJobIdAsync(filter, ct);
                            if (parentJobId is null)
                            {
                                return NotFound();
                            }
                        }

                        var query = new ListJobsQuery(
                            JobNamespace: QueryBinding.Text(http.Request.Query, "jobNamespace"),
                            Status: status,
                            JobName: QueryBinding.Text(http.Request.Query, "jobName"),
                            ParentJobId: parentJobId,
                            TenantId: tenantId,
                            TenantKey: QueryBinding.Text(http.Request.Query, "tenantKey"),
                            CorrelationKey: QueryBinding.Text(http.Request.Query, "correlationKey"),
                            PageSize: pageSize,
                            Cursor: QueryBinding.Text(http.Request.Query, "cursor"),
                            IncludeTotal: includeTotal ?? false,
                            Tags: QueryBinding.Tags(http.Request.Query),
                            TerminalOnly: terminalOnly,
                            RecurringOnly: recurringOnly
                        );
                        return Results.Json(
                            await operations.Ledger.ListJobsAsync(query, ct),
                            DashboardJsonContext.Default.PagedResultJobListItem
                        );
                    });
                }
            )
            .Produces<PagedResult<JobListItem>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group
            .MapGet(
                "/jobs/{jobRef}",
                async (string jobRef, IJobs jobs, CancellationToken ct) =>
                {
                    if (!JobTargetBinding.TryParseTarget(jobRef, options, out var lookup))
                    {
                        return NotFound();
                    }

                    var snapshot = await jobs.GetAsync(lookup, ct);
                    return snapshot is null ? NotFound() : Results.Json(snapshot, DashboardJsonContext.Default.JobSnapshot);
                }
            )
            .Produces<JobSnapshot>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // The stored input on its own, for callers that want only the payload (the enqueue screen's
        // clone prefill) and would otherwise pay the whole detail composition for one field.
        group
            .MapGet(
                "/jobs/{jobRef}/input",
                async Task<IResult> (string jobRef, IJobs jobs, IOptions<JobsOptions> jobsOptions, CancellationToken ct) =>
                {
                    if (!JobTargetBinding.TryParseTarget(jobRef, options, out var lookup))
                    {
                        return NotFound();
                    }

                    var snapshot = await jobs.GetAsync(lookup, ct);
                    if (snapshot is null)
                    {
                        return NotFound();
                    }

                    var input = await jobs.GetInputAsync(JobLookup.ById(snapshot.JobId), ct);
                    return Results.Json(
                        JobPayloadResponse.From(input ?? JobPayload.None, jobsOptions.Value.MaxInlinePayloadBytes),
                        DashboardJsonContext.Default.JobPayloadResponse
                    );
                }
            )
            .Produces<Features.Jobs.JobPayloadResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // One aggregate that renders the whole job screen: the snapshot plus every depth read a
        // lightweight job needs (input/result/checkpoints/explain/lineage/schedules/eligible
        // workers), composed from the IJobs reads off one snapshot GET. The unbounded event history
        // keeps its own paged endpoint below.
        group
            .MapGet(
                "/jobs/{jobRef}/detail",
                async Task<IResult> (
                    string jobRef,
                    IJobs jobs,
                    IActaOperations operations,
                    IOptions<JobsOptions> jobsOptions,
                    CancellationToken ct
                ) =>
                {
                    if (!JobTargetBinding.TryParseTarget(jobRef, options, out var lookup))
                    {
                        return NotFound();
                    }

                    var snapshot = await jobs.GetAsync(lookup, ct);
                    if (snapshot is null)
                    {
                        return NotFound();
                    }

                    var detail = await JobDetailResponse.ComposeAsync(
                        jobs,
                        operations,
                        snapshot,
                        jobsOptions.Value.MaxInlinePayloadBytes,
                        ct
                    );
                    return Results.Json(detail, DashboardJsonContext.Default.JobDetailResponse);
                }
            )
            .Produces<Features.Jobs.JobDetailResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // A lookup, not a filter, and deliberately its own route. Both parameters are required and
        // together identify at most one job, so this answers with a single object or 404. Folding it
        // into /jobs would make one route return either a page or a job depending on which query
        // parameters arrived, which is a worse contract than the path segment it would remove.
        group
            .MapGet(
                "/jobs/by-key",
                static (HttpContext http, IJobs jobs, CancellationToken ct) =>
                {
                    var jobNamespace = QueryBinding.Text(http.Request.Query, "jobNamespace");
                    var deduplicationKey = QueryBinding.Text(http.Request.Query, "deduplicationKey");
                    return jobNamespace is null || deduplicationKey is null
                        ? Task.FromResult(BadRequest("jobNamespace and deduplicationKey are required."))
                        : Guard(async () =>
                        {
                            var snapshot = await jobs.GetAsync(JobLookup.ByDeduplicationKey(jobNamespace, deduplicationKey), ct);
                            return snapshot is null
                                ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Job not found.")
                                : Results.Json(snapshot, DashboardJsonContext.Default.JobSnapshot);
                        });
                }
            )
            .Produces<JobSnapshot>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group
            .MapGet(
                "/overview",
                static (HttpContext http, IActaOperations operations, IOptions<JobsOptions> jobs, CancellationToken ct) =>
                {
                    // Staleness and executor capacity key on this deployment's own lease window rather than
                    // the query default: past the lease a worker's jobs are already reclaimable, so its
                    // slots are not capacity. A host that widens the lease widens both readings with it.
                    var query = new OverviewQuery(
                        JobNamespace: QueryBinding.Text(http.Request.Query, "jobNamespace"),
                        StaleWorkerAfterSeconds: jobs.Value.LeaseTtlSeconds
                    );
                    return Guard(async () =>
                        Results.Json(await operations.Ledger.GetOverviewAsync(query, ct), DashboardJsonContext.Default.OverviewSnapshot)
                    );
                }
            )
            .Produces<OverviewSnapshot>(StatusCodes.Status200OK);

        // The overview's outbox lens: each namespace's sys.outbox slot result (the relay's persisted
        // tick summary) so the health verdict can show source lag from ledger reads alone.
        group
            .MapGet(
                "/overview/outbox",
                static (HttpContext http, IJobs jobs, IActaOperations operations, CancellationToken ct) =>
                {
                    var jobNamespace = QueryBinding.Text(http.Request.Query, "jobNamespace");
                    return Guard(async () =>
                        Results.Json(
                            await ComposeOutboxLinesAsync(jobs, operations, jobNamespace, ct),
                            DashboardJsonContext.Default.IReadOnlyListOverviewOutboxLine
                        )
                    );
                }
            )
            .Produces<IReadOnlyList<OverviewOutboxLine>>(StatusCodes.Status200OK);

        group
            .MapGet(
                "/namespaces",
                static (HttpContext http, IActaOperations operations, CancellationToken ct) =>
                {
                    string? error = null;
                    if (
                        !QueryBinding.TryInt(http.Request.Query, "pageSize", out var pageSize, ref error)
                        || !QueryBinding.TryBool(http.Request.Query, "includeTotal", out var includeTotal, ref error)
                        || !QueryBinding.TryEnum<JobNamespaceStatusCode>(http.Request.Query, "status", out var status, ref error)
                    )
                    {
                        return Task.FromResult(BadRequest(error));
                    }

                    var query = new ListNamespacesQuery(
                        NameContains: QueryBinding.Text(http.Request.Query, "nameContains"),
                        Status: status,
                        PageSize: pageSize,
                        Cursor: QueryBinding.Text(http.Request.Query, "cursor"),
                        IncludeTotal: includeTotal ?? false,
                        Tags: QueryBinding.Tags(http.Request.Query)
                    );
                    return Guard(async () =>
                        Results.Json(
                            await operations.Namespaces.ListItemsAsync(query, ct),
                            DashboardJsonContext.Default.PagedResultNamespaceListItem
                        )
                    );
                }
            )
            .Produces<PagedResult<NamespaceListItem>>(StatusCodes.Status200OK);

        group
            .MapGet(
                "/tenants",
                static (HttpContext http, IActaOperations operations, CancellationToken ct) =>
                {
                    string? error = null;
                    if (
                        !QueryBinding.TryInt(http.Request.Query, "pageSize", out var pageSize, ref error)
                        || !QueryBinding.TryBool(http.Request.Query, "includeTotal", out var includeTotal, ref error)
                        || !QueryBinding.TryEnum<TenantStatusCode>(http.Request.Query, "status", out var status, ref error)
                    )
                    {
                        return Task.FromResult(BadRequest(error));
                    }

                    var query = new ListTenantsQuery(
                        Search: QueryBinding.Text(http.Request.Query, "search"),
                        Status: status,
                        PageSize: pageSize,
                        Cursor: QueryBinding.Text(http.Request.Query, "cursor"),
                        IncludeTotal: includeTotal ?? false,
                        Tags: QueryBinding.Tags(http.Request.Query)
                    );
                    return Guard(async () =>
                        Results.Json(await operations.Tenants.ListAsync(query, ct), DashboardJsonContext.Default.PagedResultTenantListItem)
                    );
                }
            )
            .Produces<PagedResult<TenantListItem>>(StatusCodes.Status200OK);

        group
            .MapGet(
                "/tenants/{tenantKey}",
                async (string tenantKey, IActaOperations operations, CancellationToken ct) =>
                {
                    TenantListItem? tenant;
                    try
                    {
                        tenant = await operations.Tenants.GetAsync(tenantKey, ct);
                    }
                    catch (ArgumentException)
                    {
                        return NotFound();
                    }

                    return tenant is null ? NotFound() : Results.Json(tenant, DashboardJsonContext.Default.TenantListItem);
                }
            )
            .Produces<TenantListItem>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group
            .MapGet(
                "/events",
                static (HttpContext http, IActaOperations operations, CancellationToken ct) =>
                {
                    string? error = null;
                    if (
                        !QueryBinding.TryInt(http.Request.Query, "pageSize", out var pageSize, ref error)
                        || !QueryBinding.TryEventCode(http.Request.Query, "eventCode", out var eventCode, ref error)
                        || !QueryBinding.TryLong(http.Request.Query, "jobId", out var jobId, ref error)
                        || !QueryBinding.TryInt(http.Request.Query, "tenantId", out var tenantId, ref error)
                        || !QueryBinding.TryInt(http.Request.Query, "workerId", out var workerId, ref error)
                        || !QueryBinding.TryCode<JobActorCode>(
                            http.Request.Query,
                            "actorCode",
                            JobActorCodeExtensions.FromCode,
                            out var actorCode,
                            ref error
                        )
                        || !QueryBinding.TryCode<JobEventReasonCode>(
                            http.Request.Query,
                            "reasonCode",
                            JobEventReasonCodeExtensions.FromCode,
                            out var reasonCode,
                            ref error
                        )
                        || !QueryBinding.TryDateTime(http.Request.Query, "createdFromUtc", out var createdFromUtc, ref error)
                        || !QueryBinding.TryDateTime(http.Request.Query, "createdToUtc", out var createdToUtc, ref error)
                    )
                    {
                        return Task.FromResult(BadRequest(error));
                    }

                    var query = new ListJobEventsQuery(
                        JobId: jobId,
                        JobNamespace: QueryBinding.Text(http.Request.Query, "jobNamespace"),
                        EventCode: eventCode,
                        TenantId: tenantId,
                        TenantKey: QueryBinding.Text(http.Request.Query, "tenantKey"),
                        WorkerId: workerId,
                        ActorCode: actorCode,
                        ReasonCode: reasonCode,
                        CreatedFromUtc: createdFromUtc,
                        CreatedToUtc: createdToUtc,
                        PageSize: pageSize,
                        Cursor: QueryBinding.Text(http.Request.Query, "cursor"),
                        Tags: QueryBinding.Tags(http.Request.Query)
                    );
                    return Guard(async () =>
                        Results.Json(
                            await operations.Ledger.ListEventsAsync(query, ct),
                            DashboardJsonContext.Default.PagedResultJobEventListItem
                        )
                    );
                }
            )
            .Produces<PagedResult<JobEventListItem>>(StatusCodes.Status200OK);

        group
            .MapGet(
                "/jobs/{jobRef}/events",
                (string jobRef, HttpContext http, IJobs jobs, IActaOperations operations, CancellationToken ct) =>
                {
                    if (!JobTargetBinding.TryParseTarget(jobRef, options, out var lookup))
                    {
                        return Task.FromResult(NotFound());
                    }

                    string? error = null;
                    return
                        !QueryBinding.TryEventCode(http.Request.Query, "eventCode", out var eventCode, ref error)
                        || !QueryBinding.TryInt(http.Request.Query, "pageSize", out var pageSize, ref error)
                        || !QueryBinding.TryBool(http.Request.Query, "includeTotal", out var includeTotal, ref error)
                        ? Task.FromResult(BadRequest(error))
                        : Guard(async () =>
                        {
                            var jobId = await jobs.ResolveJobIdAsync(lookup, ct);
                            if (jobId is null)
                            {
                                return NotFound();
                            }

                            var query = new ListJobEventsQuery(
                                JobId: jobId,
                                EventCode: eventCode,
                                PageSize: pageSize,
                                Cursor: QueryBinding.Text(http.Request.Query, "cursor"),
                                IncludeTotal: includeTotal ?? false
                            );
                            return Results.Json(
                                await operations.Ledger.ListEventsAsync(query, ct),
                                DashboardJsonContext.Default.PagedResultJobEventListItem
                            );
                        });
                }
            )
            .Produces<PagedResult<JobEventListItem>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group
            .MapGet(
                "/definitions",
                static (HttpContext http, IActaOperations operations, CancellationToken ct) =>
                {
                    string? error = null;
                    if (
                        !QueryBinding.TryEnum<JobDefinitionStatusCode>(http.Request.Query, "status", out var status, ref error)
                        || !QueryBinding.TryInt(http.Request.Query, "pageSize", out var pageSize, ref error)
                        || !QueryBinding.TryBool(http.Request.Query, "includeTotal", out var includeTotal, ref error)
                    )
                    {
                        return Task.FromResult(BadRequest(error));
                    }

                    var query = new ListJobDefinitionsQuery(
                        JobNamespace: QueryBinding.Text(http.Request.Query, "jobNamespace"),
                        NameContains: QueryBinding.Text(http.Request.Query, "nameContains"),
                        Status: status,
                        PageSize: pageSize,
                        Cursor: QueryBinding.Text(http.Request.Query, "cursor"),
                        IncludeTotal: includeTotal ?? false,
                        Tags: QueryBinding.Tags(http.Request.Query)
                    );
                    return Guard(async () =>
                        Results.Json(
                            await operations.Definitions.ListAsync(query, ct),
                            DashboardJsonContext.Default.PagedResultJobDefinitionListItem
                        )
                    );
                }
            )
            .Produces<PagedResult<JobDefinitionListItem>>(StatusCodes.Status200OK);

        group
            .MapGet(
                "/definitions/{defId:int}",
                static (int defId, HttpContext http, IActaOperations operations, CancellationToken ct) =>
                    Guard(async () =>
                    {
                        var def = await operations.Definitions.GetAsync(defId, ct);
                        return def is null
                            ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Definition not found.")
                            : Results.Json(def, DashboardJsonContext.Default.JobDefinitionDetail);
                    })
            )
            .Produces<JobDefinitionDetail>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group
            .MapGet(
                "/definitions/{defId:int}/events",
                static (int defId, HttpContext http, IActaOperations operations, CancellationToken ct) =>
                {
                    string? error = null;
                    if (
                        !QueryBinding.TryEventCode(http.Request.Query, "eventCode", out var eventCode, ref error)
                        || !QueryBinding.TryInt(http.Request.Query, "pageSize", out var pageSize, ref error)
                    )
                    {
                        return Task.FromResult(BadRequest(error));
                    }

                    var query = new ListJobEventsQuery(
                        JobDefinitionId: defId,
                        EventCode: eventCode,
                        PageSize: pageSize,
                        Cursor: QueryBinding.Text(http.Request.Query, "cursor")
                    );
                    return Guard(async () =>
                        Results.Json(
                            await operations.Ledger.ListEventsAsync(query, ct),
                            DashboardJsonContext.Default.PagedResultJobEventListItem
                        )
                    );
                }
            )
            .Produces<PagedResult<JobEventListItem>>(StatusCodes.Status200OK);

        group
            .MapGet(
                "/schedules",
                static (HttpContext http, IActaOperations operations, CancellationToken ct) =>
                {
                    string? error = null;
                    if (
                        !QueryBinding.TryEnum<ScheduleOriginCode>(http.Request.Query, "origin", out var origin, ref error)
                        || !QueryBinding.TryBool(http.Request.Query, "liveOnly", out var liveOnly, ref error)
                        || !QueryBinding.TryInt(http.Request.Query, "pageSize", out var pageSize, ref error)
                        || !QueryBinding.TryBool(http.Request.Query, "includeTotal", out var includeTotal, ref error)
                    )
                    {
                        return Task.FromResult(BadRequest(error));
                    }

                    var query = new ListJobSchedulesQuery(
                        JobNamespace: QueryBinding.Text(http.Request.Query, "jobNamespace"),
                        JobName: QueryBinding.Text(http.Request.Query, "jobName"),
                        Origin: origin,
                        LiveOnly: liveOnly ?? true,
                        PageSize: pageSize,
                        Cursor: QueryBinding.Text(http.Request.Query, "cursor"),
                        IncludeTotal: includeTotal ?? false,
                        Tags: QueryBinding.Tags(http.Request.Query)
                    );
                    return Guard(async () =>
                        Results.Json(
                            await operations.Schedules.ListAsync(query, ct),
                            DashboardJsonContext.Default.PagedResultJobScheduleListItem
                        )
                    );
                }
            )
            .Produces<PagedResult<JobScheduleListItem>>(StatusCodes.Status200OK);

        group
            .MapGet(
                "/capabilities",
                (HttpContext http, IActaOperations operations) =>
                {
                    var provider = operations.Provider switch
                    {
                        DbProvider.SqlServer => "mssql",
                        DbProvider.Postgres => "pg",
                        DbProvider.Sqlite => "sqlite",
                        _ => "unknown",
                    };
                    var version =
                        typeof(IJobs).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                        ?? typeof(IJobs).Assembly.GetName().Version?.ToString()
                        ?? "unknown";
                    // The configured Acta schema qualifies the operator views the dashboard's Copy-SQL emits.
                    // Resolved optionally so a host that maps the API without a registered provider still answers
                    // (the provider package registers SqlProviderOptions); the default matches the option default.
                    var schema = http.RequestServices.GetService<SqlProviderOptions>()?.Schema ?? "acta";
                    var body = new CapabilitiesResponse(
                        ControlsEnabled: options.EnableControls,
                        Version: version,
                        Provider: provider,
                        Schema: schema,
                        ConfirmationHeader: options.ControlConfirmationHeaderName
                    );
                    return Results.Json(body, DashboardJsonContext.Default.CapabilitiesResponse);
                }
            )
            .Produces<CapabilitiesResponse>(StatusCodes.Status200OK);

        group
            .MapGet(
                "/schedules/preview",
                static (HttpContext http, IActaOperations operations, CancellationToken ct) =>
                {
                    string? error = null;
                    if (!QueryBinding.TryInt(http.Request.Query, "count", out var count, ref error))
                    {
                        return Task.FromResult(BadRequest(error));
                    }

                    var jobNamespace = QueryBinding.Text(http.Request.Query, "jobNamespace");
                    var jobName = QueryBinding.Text(http.Request.Query, "jobName");
                    var scheduleName = QueryBinding.Text(http.Request.Query, "scheduleName");
                    if (jobNamespace is null || jobName is null || scheduleName is null)
                    {
                        return Task.FromResult(BadRequest("jobNamespace, jobName, and scheduleName are required."));
                    }

                    var lookup = new JobScheduleLookup(JobLookup.ByDeduplicationKey(jobNamespace, jobName), scheduleName);
                    return Guard(async () =>
                    {
                        var preview = await operations.Schedules.PreviewAsync(lookup, count ?? 10, ct);
                        return preview is null
                            ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Schedule not found.")
                            : Results.Json(preview, DashboardJsonContext.Default.SchedulePreview);
                    });
                }
            )
            .Produces<SchedulePreview>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group
            .MapGet(
                "/workers",
                static (HttpContext http, IActaOperations operations, CancellationToken ct) =>
                {
                    string? error = null;
                    if (
                        !QueryBinding.TryEnum<WorkerStatusCode>(http.Request.Query, "status", out var status, ref error)
                        || !QueryBinding.TryInt(http.Request.Query, "pageSize", out var pageSize, ref error)
                        || !QueryBinding.TryBool(http.Request.Query, "includeTotal", out var includeTotal, ref error)
                    )
                    {
                        return Task.FromResult(BadRequest(error));
                    }

                    var query = new ListWorkersQuery(
                        JobNamespace: QueryBinding.Text(http.Request.Query, "jobNamespace"),
                        Status: status,
                        PageSize: pageSize,
                        Cursor: QueryBinding.Text(http.Request.Query, "cursor"),
                        IncludeTotal: includeTotal ?? false,
                        Tags: QueryBinding.Tags(http.Request.Query)
                    );
                    return Guard(async () =>
                        Results.Json(
                            await operations.Workers.ListAsync(query, ct),
                            DashboardJsonContext.Default.PagedResultJobWorkerListItem
                        )
                    );
                }
            )
            .Produces<PagedResult<JobWorkerListItem>>(StatusCodes.Status200OK);

        group
            .MapGet(
                "/workers/{workerId:int:min(1)}",
                static (int workerId, HttpContext http, IActaOperations operations, CancellationToken ct) =>
                    Guard(async () =>
                    {
                        var worker = await operations.Workers.GetAsync(workerId, ct);
                        return worker is null
                            ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Worker not found.")
                            : Results.Json(worker, DashboardJsonContext.Default.JobWorkerDetail);
                    })
            )
            .Produces<JobWorkerDetail>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group
            .MapGet(
                "/alerts",
                (HttpContext http, IJobs jobs, IActaOperations operations, CancellationToken ct) =>
                {
                    string? error = null;
                    if (
                        !QueryBinding.TryEnum<AlertSeverityCode>(http.Request.Query, "severityAtLeast", out var severity, ref error)
                        || !QueryBinding.TryBool(http.Request.Query, "unresolvedOnly", out var unresolvedOnly, ref error)
                        || !QueryBinding.TryEnum<AlertDeliveryStatusCode>(http.Request.Query, "deliveryStatus", out var delivery, ref error)
                        || !QueryBinding.TryBool(http.Request.Query, "acknowledged", out var acknowledged, ref error)
                        || !QueryBinding.TryInt(http.Request.Query, "pageSize", out var pageSize, ref error)
                        || !QueryBinding.TryBool(http.Request.Query, "includeTotal", out var includeTotal, ref error)
                    )
                    {
                        return Task.FromResult(BadRequest(error));
                    }

                    var jobRefText = QueryBinding.Text(http.Request.Query, "jobRef");
                    JobLookup? jobFilter = null;
                    if (jobRefText is not null)
                    {
                        if (!JobTargetBinding.TryParseTarget(jobRefText, options, out var parsed))
                        {
                            return Task.FromResult(BadRequest("jobRef is not a valid job ref."));
                        }

                        jobFilter = parsed;
                    }

                    return Guard(async () =>
                    {
                        long? jobId = null;
                        if (jobFilter is { } filter)
                        {
                            jobId = await jobs.ResolveJobIdAsync(filter, ct);
                            if (jobId is null)
                            {
                                return NotFound();
                            }
                        }

                        var query = new ListJobAlertsQuery(
                            JobNamespace: QueryBinding.Text(http.Request.Query, "jobNamespace"),
                            JobId: jobId,
                            UnresolvedOnly: unresolvedOnly,
                            SeverityAtLeast: severity,
                            DeliveryStatus: delivery,
                            Acknowledged: acknowledged,
                            PageSize: pageSize,
                            Cursor: QueryBinding.Text(http.Request.Query, "cursor"),
                            IncludeTotal: includeTotal ?? false,
                            Tags: QueryBinding.Tags(http.Request.Query)
                        );
                        return Results.Json(
                            await operations.Alerts.ListAsync(query, ct),
                            DashboardJsonContext.Default.PagedResultJobAlertListItem
                        );
                    });
                }
            )
            .Produces<PagedResult<JobAlertListItem>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    // Only the typed validation exceptions are caller errors; anything else (including a plain
    // ArgumentException thrown by a server-side bug) falls through to the sanitized 500 handler.
    private static async Task<IResult> Guard(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (ArgumentException ex) when (ex is InvalidPageCursorException or InvalidQueryException)
        {
            return BadRequest(ex.Message);
        }
    }

    // One line per namespace whose sys.outbox slot has produced a tick summary. A namespace with no
    // relay, no successful tick yet, or a non-text result contributes no line; the dedup-key hit is
    // additionally gated on the sys.outbox job name so a user job reusing the key cannot spoof a line.
    private static async Task<IReadOnlyList<OverviewOutboxLine>> ComposeOutboxLinesAsync(
        IJobs jobs,
        IActaOperations operations,
        string? jobNamespace,
        CancellationToken ct
    )
    {
        IReadOnlyList<string> namespaces = jobNamespace is not null
            ? [jobNamespace]
            : (await operations.Namespaces.ListAsync(new ListNamespacesQuery(PageSize: 100), ct)).Items;

        var lines = new List<OverviewOutboxLine>();
        foreach (var ns in namespaces)
        {
            var slot = await jobs.GetAsync(JobLookup.ByDeduplicationKey(ns, "sys.outbox"), ct);
            if (slot is not { JobName: "sys.outbox" })
            {
                continue;
            }

            var result = await jobs.GetResultAsync(JobLookup.ById(slot.JobId), ct);
            if (result is not { } payload || payload.Format.Id != JobPayloadFormat.Text.Id)
            {
                continue;
            }

            var tick = System.Text.Encoding.UTF8.GetString(payload.Data.Span);
            lines.Add(new OverviewOutboxLine(ns, slot.JobRef.ToString(), tick, ParseBacklog(tick)));
        }

        return lines;
    }

    // The backlog is the summary's last "backlog=N" token; an unparseable result reads as zero so a
    // format drift degrades the lens rather than failing the overview.
    private static long ParseBacklog(string tick)
    {
        var index = tick.LastIndexOf("backlog=", StringComparison.Ordinal);
        return index >= 0 && long.TryParse(tick.AsSpan(index + "backlog=".Length), out var value) ? value : 0;
    }

    private static IResult BadRequest(string? detail) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request.", detail: detail);

    private static IResult NotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Job not found.");
}
