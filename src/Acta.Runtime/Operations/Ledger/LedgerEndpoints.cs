using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore;

/// <summary>
/// The Ledger routes: the job list, the event list, one job's and one definition's event history, and the overview.
/// Every ref filter resolves to the internal id the ledger indexes on: a malformed ref is the 400, and a ref that names
/// no row is the 404.
/// </summary>
internal static class LedgerEndpoints
{
    public static void Map(RouteGroupBuilder reads, ActaEndpointOptions options)
    {
        MapJobs(reads, options);
        MapEvents(reads, options);
        MapHistories(reads, options);
        GetOverviewEndpoint.Map(reads);
    }

    private static void MapJobs(RouteGroupBuilder reads, ActaEndpointOptions options) =>
        reads
            .MapList(
                "/jobs",
                "List jobs, filtered and keyset-paged.",
                DashboardJsonContext.Default.PagedResultJobListItem,
                query =>
                    (
                        Query: new ListJobsQuery(
                            JobNamespace: query.Text("jobNamespace"),
                            Status: query.Enum<JobStatusCode>("status"),
                            JobName: query.Text("jobName"),
                            TenantKey: query.Text("tenantKey"),
                            CorrelationKey: query.Text("correlationKey"),
                            PageSize: query.Int("pageSize"),
                            Cursor: query.Text("cursor"),
                            IncludeTotal: query.Bool("includeTotal") ?? false,
                            Tags: query.Tags(),
                            TerminalOnly: query.Bool("terminalOnly"),
                            RecurringOnly: query.Bool("recurringOnly"),
                            Lane: query.Text("lane")
                        ),
                        Parent: query.Job("parentJobRef", options)
                    ),
                static async (call, bound) =>
                    await call.Operations.Ledger.ListJobsAsync(
                        bound.Query with
                        {
                            ParentJobId = await call.JobIdAsync(bound.Parent),
                        },
                        call.Ct
                    ),
                [
                    new QueryParameterDoc("jobNamespace", QueryParameterKind.String, "Only jobs in this namespace."),
                    new QueryParameterDoc("jobName", QueryParameterKind.String, "Exact job name."),
                    new QueryParameterDoc("status", QueryParameterKind.String, "Exact job status as a kebab code.", CodeKind: "job-status"),
                    new QueryParameterDoc("terminalOnly", QueryParameterKind.Bool, "True restricts to terminal statuses."),
                    new QueryParameterDoc("recurringOnly", QueryParameterKind.Bool, "True restricts to recurring slots."),
                    new QueryParameterDoc("parentJobRef", QueryParameterKind.String, "Only direct children of this parent job ref."),
                    new QueryParameterDoc("correlationKey", QueryParameterKind.String, "Only jobs stamped with this correlation key."),
                    new QueryParameterDoc("tenantKey", QueryParameterKind.String, "Only jobs admitted under this tenant key."),
                    new QueryParameterDoc("lane", QueryParameterKind.String, "Only jobs in this lane."),
                    .. QueryParameterDocExtensions.PagingCore,
                    .. QueryParameterDocExtensions.IncludeTotal,
                    .. QueryParameterDocExtensions.TagFilter,
                ]
            )
            .ProducesProblem(StatusCodes.Status404NotFound);

    private static void MapEvents(RouteGroupBuilder reads, ActaEndpointOptions options) =>
        reads
            .MapList(
                "/events",
                "List ledger events, filtered and keyset-paged.",
                DashboardJsonContext.Default.PagedResultEventListItem,
                query =>
                    (
                        Query: new ListEventsQuery(
                            IncludeTotal: query.Bool("includeTotal") == true,
                            JobNamespace: query.Text("jobNamespace"),
                            EventCode: query.EventCode("eventCode"),
                            TenantKey: query.Text("tenantKey"),
                            ActorCode: query.Code("actorCode", ActorCodeExtensions.FromCode),
                            ReasonCode: query.Code("reasonCode", JobEventReasonCodeExtensions.FromCode),
                            CreatedFromUtc: query.DateTime("createdFromUtc"),
                            CreatedToUtc: query.DateTime("createdToUtc"),
                            PageSize: query.Int("pageSize"),
                            Cursor: query.Text("cursor"),
                            Tags: query.Tags()
                        ),
                        Job: query.Job("jobRef", options),
                        Lineage: query.Job("lineageRootJobRef", options),
                        Worker: query.Worker("workerRef")
                    ),
                static async (call, bound) =>
                    await call.Operations.Ledger.ListEventsAsync(
                        bound.Query with
                        {
                            JobId = await call.JobIdAsync(bound.Job),
                            LineageRootId = await call.JobIdAsync(bound.Lineage),
                            WorkerId = bound.Worker is { } worker
                                ? (
                                    await call.Operations.Workers.GetAsync(worker, call.Ct) ?? throw new TargetNotFoundException("Worker")
                                ).WorkerId
                                : null,
                        },
                        call.Ct
                    ),
                [
                    new QueryParameterDoc("jobNamespace", QueryParameterKind.String, "Only events recorded in this namespace."),
                    new QueryParameterDoc("jobRef", QueryParameterKind.String, "Only events for this job ref."),
                    new QueryParameterDoc("lineageRootJobRef", QueryParameterKind.String, "Only events in this lineage root's tree."),
                    new QueryParameterDoc(
                        "eventCode",
                        QueryParameterKind.String,
                        "Exact event code as its kebab string.",
                        CodeKind: "event"
                    ),
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
                ]
            )
            .ProducesProblem(StatusCodes.Status404NotFound);

    private static void MapHistories(RouteGroupBuilder reads, ActaEndpointOptions options)
    {
        reads
            .MapList(
                "/jobs/{jobRef}/events",
                "List one job's event timeline.",
                DashboardJsonContext.Default.PagedResultEventListItem,
                query =>
                    (
                        Job: RouteKeys.Job(query.Http, options),
                        Query: new ListEventsQuery(
                            EventCode: query.EventCode("eventCode"),
                            PageSize: query.Int("pageSize"),
                            Cursor: query.Text("cursor"),
                            IncludeTotal: query.Bool("includeTotal") ?? false
                        )
                    ),
                static async (call, bound) =>
                    await call.Operations.Ledger.ListEventsAsync(bound.Query with { JobId = await call.JobIdAsync(bound.Job) }, call.Ct),
                [
                    new QueryParameterDoc(
                        "eventCode",
                        QueryParameterKind.String,
                        "Exact event code as its kebab string.",
                        CodeKind: "event"
                    ),
                    .. QueryParameterDocExtensions.PagingCore,
                    .. QueryParameterDocExtensions.IncludeTotal,
                ]
            )
            .ProducesProblem(StatusCodes.Status404NotFound);

        // The audit trail keys on the catalog id, resolved from the natural key so the id stays off the wire; a key
        // that names no definition is the same 404 the definition read answers.
        reads
            .MapList(
                "/definitions/{jobNamespace}/{jobName}/events",
                "List a definition's ledger events (its jobs' lineage, not just changes).",
                DashboardJsonContext.Default.PagedResultEventListItem,
                static query =>
                    (
                        Namespace: query.Route("jobNamespace"),
                        Name: query.Route("jobName"),
                        Query: new ListEventsQuery(
                            EventCode: query.EventCode("eventCode"),
                            PageSize: query.Int("pageSize"),
                            Cursor: query.Text("cursor")
                        )
                    ),
                static async (call, bound) =>
                {
                    var definition =
                        await call.Operations.Definitions.GetAsync(bound.Namespace, bound.Name, call.Ct)
                        ?? throw new TargetNotFoundException("Definition");
                    return await call.Operations.Ledger.ListEventsAsync(
                        bound.Query with
                        {
                            DefinitionId = definition.DefinitionId,
                        },
                        call.Ct
                    );
                },
                [
                    new QueryParameterDoc(
                        "eventCode",
                        QueryParameterKind.String,
                        "Exact event code as its kebab string.",
                        CodeKind: "event"
                    ),
                    new QueryParameterDoc("pageSize", QueryParameterKind.Int, "Rows per page; the server clamps to its bounds."),
                    new QueryParameterDoc(
                        "cursor",
                        QueryParameterKind.String,
                        "Opaque keyset cursor from the previous page's nextCursor; omit for the first page."
                    ),
                ]
            )
            .ProducesProblem(StatusCodes.Status404NotFound);
    }
}
