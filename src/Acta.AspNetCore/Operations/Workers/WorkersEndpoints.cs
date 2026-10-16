using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore;

/// <summary>The Workers routes: the list and one worker.</summary>
internal static class WorkersEndpoints
{
    public static void Map(RouteGroupBuilder reads)
    {
        reads.MapList(
            "/workers",
            "List workers.",
            DashboardJsonContext.Default.ListWorkersResponse,
            static query => new ListWorkersQuery(
                JobNamespace: query.Text("jobNamespace"),
                Status: query.Enum<WorkerStatusCode>("status"),
                PageSize: query.Int("pageSize"),
                Cursor: query.Text("cursor"),
                IncludeTotal: query.Bool("includeTotal") ?? false
            ),
            static (call, query) => call.Operations.Workers.ListAsync(query, call.Ct),
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
            ]
        );
        reads.MapDetail(
            "/workers/{workerRef}",
            "Read one worker.",
            "Worker",
            DashboardJsonContext.Default.GetWorkerResponse,
            static (http, call) => call.Operations.Workers.GetAsync(RouteKeys.Worker(http), call.Ct)
        );
    }
}
