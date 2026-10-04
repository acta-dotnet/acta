using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore;

/// <summary>The Workers routes: the list, one worker, and its tags.</summary>
internal static class WorkersEndpoints
{
    public static void Map(RouteGroupBuilder reads, RouteGroupBuilder? controls, ActaEndpointOptions options)
    {
        reads.MapList(
            "/workers",
            "List workers.",
            DashboardJsonContext.Default.PagedResultWorkerListItem,
            static query => new ListWorkersQuery(
                JobNamespace: query.Text("jobNamespace"),
                Status: query.Enum<WorkerStatusCode>("status"),
                PageSize: query.Int("pageSize"),
                Cursor: query.Text("cursor"),
                IncludeTotal: query.Bool("includeTotal") ?? false,
                Tags: query.Tags()
            ),
            static (operations, query, ct) => operations.Workers.ListAsync(query, ct),
            [
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
            ]
        );
        reads.MapDetail(
            "/workers/{workerRef}",
            "Read one worker.",
            "Worker",
            DashboardJsonContext.Default.WorkerDetail,
            static (http, operations, ct) => operations.Workers.GetAsync(RouteKeys.Worker(http), ct)
        );
        reads.MapTags(controls, "/workers/{workerRef}/tags", "worker", options, static http => TagTarget.ForWorker(RouteKeys.Worker(http)));
    }
}
