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
        //
        // 415 is deliberately not here. Unlike these four it has no group-level filter: it is
        // produced inside ControlEndpointValidation's four body readers, which only a handler that
        // reads a body calls. Declaring it here would promise it on some forty GET reads that cannot
        // execute that code, so each body-reading route declares it where its body is read.
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
            OutboxControlEndpoints.Map(controls, options);
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
                        || !QueryBinding.TryInt(http.Request.Query, "pageSize", out var pageSize, ref error)
                        || !QueryBinding.TryBool(http.Request.Query, "includeTotal", out var includeTotal, ref error)
                        || !QueryBinding.TryBool(http.Request.Query, "terminalOnly", out var terminalOnly, ref error)
                        || !QueryBinding.TryBool(http.Request.Query, "recurringOnly", out var recurringOnly, ref error)
                    )
                    {
                        return Task.FromResult(BadRequest(error));
                    }

                    var parentJobRefText = QueryBinding.Text(http.Request.Query, "parentJobRef");
                    JobLookup? parentFilter = null;
                    if (parentJobRefText is not null)
                    {
                        if (!JobTargetBinding.TryParseTarget(parentJobRefText, options, out var parsed))
                        {
                            return Task.FromResult(BadRequest("parentJobRef is not a valid job ref."));
                        }

                        parentFilter = parsed;
                    }

                    return Guard(async () =>
                    {
                        long? parentJobId = null;
                        if (parentFilter is { } filter)
                        {
                            parentJobId = await jobs.GetJobIdAsync(filter, ct);
                            if (parentJobId is null)
                            {
                                return NotFound("Job");
                            }
                        }

                        var query = new ListJobsQuery(
                            JobNamespace: QueryBinding.Text(http.Request.Query, "jobNamespace"),
                            Status: status,
                            JobName: QueryBinding.Text(http.Request.Query, "jobName"),
                            ParentJobId: parentJobId,
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
            .WithSummary("List jobs, filtered and keyset-paged.")
            .Produces<PagedResult<JobListItem>>(StatusCodes.Status200OK)
            .WithQueryParameters([
                new QueryParameterDoc("jobNamespace", QueryParameterKind.String, "Only jobs in this namespace."),
                new QueryParameterDoc("jobName", QueryParameterKind.String, "Exact job name."),
                new QueryParameterDoc("status", QueryParameterKind.String, "Exact job status as a kebab code.", CodeKind: "job-status"),
                new QueryParameterDoc("terminalOnly", QueryParameterKind.Bool, "True restricts to terminal statuses."),
                new QueryParameterDoc("recurringOnly", QueryParameterKind.Bool, "True restricts to recurring slots."),
                new QueryParameterDoc("parentJobRef", QueryParameterKind.String, "Only direct children of this parent job ref."),
                new QueryParameterDoc("correlationKey", QueryParameterKind.String, "Only jobs stamped with this correlation key."),
                new QueryParameterDoc("tenantKey", QueryParameterKind.String, "Only jobs admitted under this tenant key."),
                .. QueryParameterDocExtensions.PagingCore,
                .. QueryParameterDocExtensions.IncludeTotal,
                .. QueryParameterDocExtensions.TagFilter,
            ])
            .ProducesProblem(StatusCodes.Status404NotFound);

        group
            .MapGet(
                "/jobs/{jobRef}",
                async (string jobRef, IJobs jobs, CancellationToken ct) =>
                {
                    if (!JobTargetBinding.TryParseTarget(jobRef, options, out var lookup))
                    {
                        return RefSegment.Malformed("jobRef", "job");
                    }

                    var snapshot = await jobs.GetAsync(lookup, ct);
                    return snapshot is null ? NotFound("Job") : Results.Json(snapshot, DashboardJsonContext.Default.JobDetail);
                }
            )
            .WithSummary("Read one job.")
            .Produces<JobDetail>(StatusCodes.Status200OK)
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
                        return RefSegment.Malformed("jobRef", "job");
                    }

                    var snapshot = await jobs.GetAsync(lookup, ct);
                    if (snapshot is null)
                    {
                        return NotFound("Job");
                    }

                    var input = await jobs.GetInputAsync(JobLookup.ById(snapshot.JobId), ct);
                    return Results.Json(
                        JobPayloadResponse.From(input ?? JobPayload.None, jobsOptions.Value.MaxInlinePayloadBytes),
                        DashboardJsonContext.Default.JobPayloadResponse
                    );
                }
            )
            .WithSummary("Read the job's stored input payload.")
            .Produces<Features.Jobs.JobPayloadResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // One aggregate that renders the whole job screen: the job row plus every depth read a
        // lightweight job needs (input/result/checkpoints/explain/lineage/schedules/eligible
        // workers), composed from the IJobs reads off one row GET. The unbounded event history
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
                        return RefSegment.Malformed("jobRef", "job");
                    }

                    var snapshot = await jobs.GetAsync(lookup, ct);
                    if (snapshot is null)
                    {
                        return NotFound("Job");
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
            .WithSummary("Read the job's full detail aggregate in one request.")
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
                                : Results.Json(snapshot, DashboardJsonContext.Default.JobDetail);
                        });
                }
            )
            .WithSummary("Resolve a job by namespace and deduplication key.")
            .Produces<JobDetail>(StatusCodes.Status200OK)
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
            ])
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
            .WithSummary("Read the ledger's health-overview counters.")
            .Produces<OverviewSnapshot>(StatusCodes.Status200OK)
            .WithQueryParameters([
                new QueryParameterDoc("jobNamespace", QueryParameterKind.String, "Scope the overview to one namespace; omit for all."),
            ]);

        // The outbox operator surface's read half. Sources compose cross-peer from each namespace's
        // sys.outbox slot result (the relay's persisted tick summary), so any host with ledger access
        // answers; the quarantine listing opens the producer's source database and therefore answers
        // only where the namespace's relay is registered (sources report isLocal for discovery).
        group
            .MapGet(
                "/outbox/sources",
                static (HttpContext http, IActaOperations operations, CancellationToken ct) =>
                {
                    string? error = null;
                    if (!QueryBinding.TryInt(http.Request.Query, "pageSize", out var pageSize, ref error))
                    {
                        return Task.FromResult(BadRequest(error));
                    }

                    var query = new ListOutboxSourcesQuery(
                        JobNamespace: QueryBinding.Text(http.Request.Query, "jobNamespace"),
                        PageSize: pageSize,
                        Cursor: QueryBinding.Text(http.Request.Query, "cursor")
                    );
                    return Guard(async () =>
                        Results.Json(
                            await operations.Outbox.ListSourcesAsync(query, ct),
                            DashboardJsonContext.Default.PagedResultOutboxSourceListItem
                        )
                    );
                }
            )
            .WithSummary("List outbox relay sources with backlog and quarantine totals.")
            .Produces<PagedResult<OutboxSourceListItem>>(StatusCodes.Status200OK)
            .WithQueryParameters([
                new QueryParameterDoc("jobNamespace", QueryParameterKind.String, "Scope to one relay source's namespace; omit for all."),
                .. QueryParameterDocExtensions.PagingCore,
            ]);

        group
            .MapGet(
                "/outbox/{jobNamespace}/quarantined",
                static (string jobNamespace, HttpContext http, IActaOperations operations, CancellationToken ct) =>
                {
                    string? error = null;
                    if (
                        !QueryBinding.TryInt(http.Request.Query, "pageSize", out var pageSize, ref error)
                        || !QueryBinding.TryBool(http.Request.Query, "includeTotal", out var includeTotal, ref error)
                    )
                    {
                        return Task.FromResult(BadRequest(error));
                    }

                    var query = new ListOutboxQuarantinedQuery(
                        jobNamespace,
                        PageSize: pageSize,
                        Cursor: QueryBinding.Text(http.Request.Query, "cursor"),
                        IncludeTotal: includeTotal ?? false
                    );
                    return Guard(async () =>
                    {
                        try
                        {
                            return Results.Json(
                                await operations.Outbox.ListQuarantinedAsync(query, ct),
                                DashboardJsonContext.Default.PagedResultOutboxQuarantinedItem
                            );
                        }
                        catch (InvalidOperationException ex)
                        {
                            // The source is not registered on this host: a placement constraint, not a
                            // fault, so it answers 409 with the verbatim explanation.
                            return Results.Problem(
                                statusCode: StatusCodes.Status409Conflict,
                                title: "Quarantine listing unavailable on this host.",
                                detail: ex.Message
                            );
                        }
                    });
                }
            )
            .WithSummary("List a relay source's quarantined staging rows.")
            .Produces<PagedResult<OutboxQuarantinedItem>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithQueryParameters([.. QueryParameterDocExtensions.PagingCore, .. QueryParameterDocExtensions.IncludeTotal]);

        group
            .MapGet(
                "/namespaces",
                static (HttpContext http, IActaOperations operations, CancellationToken ct) =>
                {
                    string? error = null;
                    if (
                        !QueryBinding.TryInt(http.Request.Query, "pageSize", out var pageSize, ref error)
                        || !QueryBinding.TryBool(http.Request.Query, "includeTotal", out var includeTotal, ref error)
                        || !QueryBinding.TryEnum<NamespaceStatusCode>(http.Request.Query, "status", out var status, ref error)
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
                            await operations.Namespaces.ListAsync(query, ct),
                            DashboardJsonContext.Default.PagedResultNamespaceListItem
                        )
                    );
                }
            )
            .WithSummary("List namespaces.")
            .Produces<PagedResult<NamespaceListItem>>(StatusCodes.Status200OK)
            .WithQueryParameters([
                new QueryParameterDoc(
                    "status",
                    QueryParameterKind.String,
                    "Exact namespace status as a kebab code.",
                    CodeKind: "namespace-status"
                ),
                new QueryParameterDoc("nameContains", QueryParameterKind.String, "Case-insensitive substring match on the namespace name."),
                .. QueryParameterDocExtensions.PagingCore,
                .. QueryParameterDocExtensions.IncludeTotal,
                .. QueryParameterDocExtensions.TagFilter,
            ]);

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
                        NameContains: QueryBinding.Text(http.Request.Query, "nameContains"),
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
            .WithSummary("List tenants.")
            .Produces<PagedResult<TenantListItem>>(StatusCodes.Status200OK)
            .WithQueryParameters([
                new QueryParameterDoc(
                    "status",
                    QueryParameterKind.String,
                    "Exact tenant status as a kebab code.",
                    CodeKind: "tenant-status"
                ),
                new QueryParameterDoc(
                    "nameContains",
                    QueryParameterKind.String,
                    "Case-insensitive substring matched against key, display name, or description."
                ),
                .. QueryParameterDocExtensions.PagingCore,
                .. QueryParameterDocExtensions.IncludeTotal,
                .. QueryParameterDocExtensions.TagFilter,
            ]);

        group
            .MapGet(
                "/tenants/{tenantKey}",
                async (string tenantKey, IActaOperations operations, CancellationToken ct) =>
                {
                    TenantDetail? tenant;
                    try
                    {
                        tenant = await operations.Tenants.GetAsync(tenantKey, ct);
                    }
                    catch (ArgumentException)
                    {
                        return NotFound("Tenant");
                    }

                    return tenant is null ? NotFound("Tenant") : Results.Json(tenant, DashboardJsonContext.Default.TenantDetail);
                }
            )
            .WithSummary("Read one tenant.")
            .Produces<TenantDetail>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group
            .MapGet(
                "/events",
                (HttpContext http, IJobs jobs, IActaOperations operations, CancellationToken ct) =>
                {
                    string? error = null;
                    if (
                        !QueryBinding.TryInt(http.Request.Query, "pageSize", out var pageSize, ref error)
                        || !QueryBinding.TryEventCode(http.Request.Query, "eventCode", out var eventCode, ref error)
                        || !QueryBinding.TryBool(http.Request.Query, "includeTotal", out var includeTotal, ref error)
                        || !QueryBinding.TryCode<ActorCode>(
                            http.Request.Query,
                            "actorCode",
                            ActorCodeExtensions.FromCode,
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

                    var jobRefText = QueryBinding.Text(http.Request.Query, "jobRef");
                    var lineageRefText = QueryBinding.Text(http.Request.Query, "lineageRootJobRef");
                    if (
                        !TryParseJobFilter(jobRefText, options, "jobRef", out var jobFilter, ref error)
                        || !TryParseJobFilter(lineageRefText, options, "lineageRootJobRef", out var lineageFilter, ref error)
                    )
                    {
                        return Task.FromResult(BadRequest(error));
                    }

                    var workerRefText = QueryBinding.Text(http.Request.Query, "workerRef");
                    WorkerRef? workerFilter = null;
                    if (workerRefText is not null)
                    {
                        if (!WorkerRef.TryParse(workerRefText, out var parsedWorker))
                        {
                            return Task.FromResult(BadRequest("workerRef is not a valid worker ref."));
                        }

                        workerFilter = parsedWorker;
                    }

                    return Guard(async () =>
                    {
                        // Every ref filter resolves to the internal id the ledger indexes on, and answers
                        // the same two ways the other ref-valued filters do (/jobs parentJobRef,
                        // /alerts jobRef): malformed is a 400 above, a ref that names no row is a 404.
                        long? jobId = null;
                        if (jobFilter is { } job)
                        {
                            jobId = await jobs.GetJobIdAsync(job, ct);
                            if (jobId is null)
                            {
                                return NotFound("Job");
                            }
                        }

                        long? lineageRootId = null;
                        if (lineageFilter is { } lineage)
                        {
                            lineageRootId = await jobs.GetJobIdAsync(lineage, ct);
                            if (lineageRootId is null)
                            {
                                return NotFound("Job");
                            }
                        }

                        int? workerId = null;
                        if (workerFilter is { } worker)
                        {
                            var row = await operations.Workers.GetAsync(worker, ct);
                            if (row is null)
                            {
                                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Worker not found.");
                            }

                            workerId = row.WorkerId;
                        }

                        var query = new ListEventsQuery(
                            JobId: jobId,
                            LineageRootId: lineageRootId,
                            IncludeTotal: includeTotal == true,
                            JobNamespace: QueryBinding.Text(http.Request.Query, "jobNamespace"),
                            EventCode: eventCode,
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
                        return Results.Json(
                            await operations.Ledger.ListEventsAsync(query, ct),
                            DashboardJsonContext.Default.PagedResultEventListItem
                        );
                    });
                }
            )
            .WithSummary("List ledger events, filtered and keyset-paged.")
            .Produces<PagedResult<EventListItem>>(StatusCodes.Status200OK)
            .WithQueryParameters([
                new QueryParameterDoc("jobNamespace", QueryParameterKind.String, "Only events recorded in this namespace."),
                new QueryParameterDoc("jobRef", QueryParameterKind.String, "Only events for this job ref."),
                new QueryParameterDoc("lineageRootJobRef", QueryParameterKind.String, "Only events in this lineage root's tree."),
                new QueryParameterDoc("eventCode", QueryParameterKind.String, "Exact event code as its kebab string.", CodeKind: "event"),
                new QueryParameterDoc("actorCode", QueryParameterKind.String, "Exact actor as its kebab code.", CodeKind: "actor"),
                new QueryParameterDoc(
                    "reasonCode",
                    QueryParameterKind.String,
                    "Exact reason as its kebab code.",
                    CodeKind: "job-event-reason"
                ),
                new QueryParameterDoc("tenantKey", QueryParameterKind.String, "Only events stamped with this tenant key."),
                new QueryParameterDoc("workerRef", QueryParameterKind.String, "Only events recorded by this worker ref."),
                new QueryParameterDoc("createdFromUtc", QueryParameterKind.Instant, "Inclusive lower bound on the event instant."),
                new QueryParameterDoc("createdToUtc", QueryParameterKind.Instant, "Exclusive upper bound on the event instant."),
                .. QueryParameterDocExtensions.PagingCore,
                new QueryParameterDoc(
                    "includeTotal",
                    QueryParameterKind.Bool,
                    "Also compute the row count; this endpoint accepts it only together with jobRef."
                ),
                .. QueryParameterDocExtensions.TagFilter,
            ])
            .ProducesProblem(StatusCodes.Status404NotFound);

        group
            .MapGet(
                "/jobs/{jobRef}/events",
                (string jobRef, HttpContext http, IJobs jobs, IActaOperations operations, CancellationToken ct) =>
                {
                    if (!JobTargetBinding.TryParseTarget(jobRef, options, out var lookup))
                    {
                        return Task.FromResult(RefSegment.Malformed("jobRef", "job"));
                    }

                    string? error = null;
                    return
                        !QueryBinding.TryEventCode(http.Request.Query, "eventCode", out var eventCode, ref error)
                        || !QueryBinding.TryInt(http.Request.Query, "pageSize", out var pageSize, ref error)
                        || !QueryBinding.TryBool(http.Request.Query, "includeTotal", out var includeTotal, ref error)
                        ? Task.FromResult(BadRequest(error))
                        : Guard(async () =>
                        {
                            var jobId = await jobs.GetJobIdAsync(lookup, ct);
                            if (jobId is null)
                            {
                                return NotFound("Job");
                            }

                            var query = new ListEventsQuery(
                                JobId: jobId,
                                EventCode: eventCode,
                                PageSize: pageSize,
                                Cursor: QueryBinding.Text(http.Request.Query, "cursor"),
                                IncludeTotal: includeTotal ?? false
                            );
                            return Results.Json(
                                await operations.Ledger.ListEventsAsync(query, ct),
                                DashboardJsonContext.Default.PagedResultEventListItem
                            );
                        });
                }
            )
            .WithSummary("List one job's event timeline.")
            .Produces<PagedResult<EventListItem>>(StatusCodes.Status200OK)
            .WithQueryParameters([
                new QueryParameterDoc("eventCode", QueryParameterKind.String, "Exact event code as its kebab string.", CodeKind: "event"),
                .. QueryParameterDocExtensions.PagingCore,
                .. QueryParameterDocExtensions.IncludeTotal,
            ])
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

                    var query = new ListDefinitionsQuery(
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
            .WithSummary("List job definitions.")
            .Produces<PagedResult<JobDefinitionListItem>>(StatusCodes.Status200OK)
            .WithQueryParameters([
                new QueryParameterDoc("jobNamespace", QueryParameterKind.String, "Only definitions registered in this namespace."),
                new QueryParameterDoc("nameContains", QueryParameterKind.String, "Case-insensitive substring match on the job name."),
                new QueryParameterDoc(
                    "status",
                    QueryParameterKind.String,
                    "Exact definition status as a kebab code.",
                    CodeKind: "job-definition-status"
                ),
                .. QueryParameterDocExtensions.PagingCore,
                .. QueryParameterDocExtensions.IncludeTotal,
                .. QueryParameterDocExtensions.TagFilter,
            ]);

        group
            .MapGet(
                "/definitions/{jobNamespace}/{jobName}",
                static (string jobNamespace, string jobName, IActaOperations operations, CancellationToken ct) =>
                    Guard(async () =>
                    {
                        var def = await operations.Definitions.GetAsync(jobNamespace, jobName, ct);
                        return def is null
                            ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Definition not found.")
                            : Results.Json(def, DashboardJsonContext.Default.JobDefinitionDetail);
                    })
            )
            .WithSummary("Read one job definition and its effective policy.")
            .Produces<JobDefinitionDetail>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group
            .MapGet(
                "/definitions/{jobNamespace}/{jobName}/events",
                static (string jobNamespace, string jobName, HttpContext http, IActaOperations operations, CancellationToken ct) =>
                {
                    string? error = null;
                    if (
                        !QueryBinding.TryEventCode(http.Request.Query, "eventCode", out var eventCode, ref error)
                        || !QueryBinding.TryInt(http.Request.Query, "pageSize", out var pageSize, ref error)
                    )
                    {
                        return Task.FromResult(BadRequest(error));
                    }

                    return Guard(async () =>
                    {
                        // The audit trail keys on the catalog id, resolved here from the natural key so
                        // the id stays off the wire. A key that names no definition is the same 404 the
                        // definition read itself answers.
                        var def = await operations.Definitions.GetAsync(jobNamespace, jobName, ct);
                        if (def is null)
                        {
                            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Definition not found.");
                        }

                        var query = new ListEventsQuery(
                            DefinitionId: def.DefinitionId,
                            EventCode: eventCode,
                            PageSize: pageSize,
                            Cursor: QueryBinding.Text(http.Request.Query, "cursor")
                        );
                        return Results.Json(
                            await operations.Ledger.ListEventsAsync(query, ct),
                            DashboardJsonContext.Default.PagedResultEventListItem
                        );
                    });
                }
            )
            .WithSummary("List a definition's ledger events (its jobs' lineage, not just changes).")
            .Produces<PagedResult<EventListItem>>(StatusCodes.Status200OK)
            .WithQueryParameters([
                new QueryParameterDoc("eventCode", QueryParameterKind.String, "Exact event code as its kebab string.", CodeKind: "event"),
                new QueryParameterDoc("pageSize", QueryParameterKind.Int, "Rows per page; the server clamps to its bounds."),
                new QueryParameterDoc(
                    "cursor",
                    QueryParameterKind.String,
                    "Opaque keyset cursor from the previous page's nextCursor; omit for the first page."
                ),
            ])
            .ProducesProblem(StatusCodes.Status404NotFound);

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

                    var query = new ListSchedulesQuery(
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
                            DashboardJsonContext.Default.PagedResultScheduleListItem
                        )
                    );
                }
            )
            .WithSummary("List recurring schedules, next run first.")
            .Produces<PagedResult<ScheduleListItem>>(StatusCodes.Status200OK)
            .WithQueryParameters([
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
            ]);

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
            .WithSummary("Read this host's API capabilities.")
            .Produces<CapabilitiesResponse>(StatusCodes.Status200OK);

        group
            .MapGet(
                "/schedules/{jobNamespace}/{jobName}/{scheduleName}/preview",
                static (
                    string jobNamespace,
                    string jobName,
                    string scheduleName,
                    HttpContext http,
                    IActaOperations operations,
                    CancellationToken ct
                ) =>
                {
                    string? error = null;
                    if (!QueryBinding.TryInt(http.Request.Query, "limit", out var count, ref error))
                    {
                        return Task.FromResult(BadRequest(error));
                    }

                    var lookup = new ScheduleLookup(JobLookup.ByDeduplicationKey(jobNamespace, jobName), scheduleName);
                    return Guard(async () =>
                    {
                        var preview = await operations.Schedules.PreviewAsync(lookup, count ?? 10, ct);
                        return preview is null
                            ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Schedule not found.")
                            : Results.Json(preview, DashboardJsonContext.Default.SchedulePreview);
                    });
                }
            )
            .WithSummary("Preview a schedule's upcoming fire instants.")
            .Produces<SchedulePreview>(StatusCodes.Status200OK)
            .WithQueryParameters([new QueryParameterDoc("limit", QueryParameterKind.Int, "How many upcoming occurrences to forecast.")])
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
                        Results.Json(await operations.Workers.ListAsync(query, ct), DashboardJsonContext.Default.PagedResultWorkerListItem)
                    );
                }
            )
            .WithSummary("List workers.")
            .Produces<PagedResult<WorkerListItem>>(StatusCodes.Status200OK)
            .WithQueryParameters([
                new QueryParameterDoc("jobNamespace", QueryParameterKind.String, "Only workers registered for this namespace."),
                new QueryParameterDoc(
                    "status",
                    QueryParameterKind.String,
                    "Exact worker status as a kebab code.",
                    CodeKind: "worker-status"
                ),
                .. QueryParameterDocExtensions.PagingCore,
                .. QueryParameterDocExtensions.IncludeTotal,
                .. QueryParameterDocExtensions.TagFilter,
            ]);

        group
            .MapGet(
                "/workers/{workerRef}",
                static (string workerRef, HttpContext http, IActaOperations operations, CancellationToken ct) =>
                    Guard(async () =>
                    {
                        if (!WorkerRef.TryParse(workerRef, out var parsed))
                        {
                            return RefSegment.Malformed("workerRef", "worker");
                        }

                        var worker = await operations.Workers.GetAsync(parsed, ct);
                        return worker is null
                            ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Worker not found.")
                            : Results.Json(worker, DashboardJsonContext.Default.WorkerDetail);
                    })
            )
            .WithSummary("Read one worker.")
            .Produces<WorkerDetail>(StatusCodes.Status200OK)
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
                        || !QueryBinding.TryBool(http.Request.Query, "acknowledgedOnly", out var acknowledged, ref error)
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
                            jobId = await jobs.GetJobIdAsync(filter, ct);
                            if (jobId is null)
                            {
                                return NotFound("Job");
                            }
                        }

                        var query = new ListAlertsQuery(
                            JobNamespace: QueryBinding.Text(http.Request.Query, "jobNamespace"),
                            JobId: jobId,
                            UnresolvedOnly: unresolvedOnly,
                            SeverityAtLeast: severity,
                            DeliveryStatus: delivery,
                            AcknowledgedOnly: acknowledged,
                            PageSize: pageSize,
                            Cursor: QueryBinding.Text(http.Request.Query, "cursor"),
                            IncludeTotal: includeTotal ?? false,
                            Tags: QueryBinding.Tags(http.Request.Query)
                        );
                        return Results.Json(
                            await operations.Alerts.ListAsync(query, ct),
                            DashboardJsonContext.Default.PagedResultAlertListItem
                        );
                    });
                }
            )
            .WithSummary("List alerts.")
            .Produces<PagedResult<AlertListItem>>(StatusCodes.Status200OK)
            .WithQueryParameters([
                new QueryParameterDoc("jobNamespace", QueryParameterKind.String, "Only alerts raised in this namespace."),
                new QueryParameterDoc("jobRef", QueryParameterKind.String, "Only alerts attached to this job ref."),
                new QueryParameterDoc(
                    "severityAtLeast",
                    QueryParameterKind.String,
                    "Severity floor as a kebab code; lower severities are excluded."
                ),
                new QueryParameterDoc("unresolvedOnly", QueryParameterKind.Bool, "True restricts to alerts with no resolved instant."),
                new QueryParameterDoc(
                    "acknowledgedOnly",
                    QueryParameterKind.Bool,
                    "True restricts to acknowledged alerts, false to unacknowledged; omit for both."
                ),
                new QueryParameterDoc(
                    "deliveryStatus",
                    QueryParameterKind.String,
                    "Exact delivery status as a kebab code.",
                    CodeKind: "alert-delivery-status"
                ),
                .. QueryParameterDocExtensions.PagingCore,
                .. QueryParameterDocExtensions.IncludeTotal,
                .. QueryParameterDocExtensions.TagFilter,
            ])
            .ProducesProblem(StatusCodes.Status404NotFound);

        group
            .MapGet(
                "/alerts/{alertRef}",
                static (string alertRef, IActaOperations operations, CancellationToken ct) =>
                    Guard(async () =>
                    {
                        if (!AlertRef.TryParse(alertRef, out var parsed))
                        {
                            return RefSegment.Malformed("alertRef", "alert");
                        }

                        var alert = await operations.Alerts.GetAsync(parsed, ct);
                        return alert is null
                            ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Alert not found.")
                            : Results.Json(alert, DashboardJsonContext.Default.AlertDetail);
                    })
            )
            .WithSummary("Read one alert.")
            .Produces<AlertDetail>(StatusCodes.Status200OK)
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

    /// <summary>
    /// Binds an optional job-ref query filter from its already-read value. Absent is null with no
    /// error; malformed is a 400, the same answer every other ref-valued query parameter gives.
    /// </summary>
    private static bool TryParseJobFilter(string? text, ActaEndpointOptions options, string name, out JobLookup? job, ref string? error)
    {
        job = null;
        if (text is null)
        {
            return true;
        }

        if (!JobTargetBinding.TryParseTarget(text, options, out var parsed))
        {
            error = $"{name} is not a valid job ref.";
            return false;
        }

        job = parsed;
        return true;
    }

    private static IResult BadRequest(string? detail) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request.", detail: detail);

    // The entity is named at every call site rather than defaulted, the way RefSegment.Malformed names
    // its entity: a shared 404 helper that assumes one noun answers every other route with the wrong one.
    private static IResult NotFound(string entity) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: $"{entity} not found.");
}
