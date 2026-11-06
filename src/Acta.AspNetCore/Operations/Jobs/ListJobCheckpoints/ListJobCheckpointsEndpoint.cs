using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Acta.AspNetCore;

/// <summary>
/// GET /jobs/{jobRef}/checkpoints: one page of the job's checkpoints, values rendered by
/// <see cref="ListJobCheckpointsResponse"/>; job detail embeds the first page and this route continues it.
/// </summary>
internal static class ListJobCheckpointsEndpoint
{
    public static void Map(RouteGroupBuilder reads, ActaEndpointOptions options) =>
        reads
            .MapGet(
                "/jobs/{jobRef}/checkpoints",
                (HttpContext http, IJobs jobs, IOptions<JobsOptions> jobsOptions, CancellationToken ct) =>
                    RouteTable.Run(async () =>
                    {
                        var job = RouteKeys.Job(http, options);
                        var paging = QueryReader.Bind(
                            http.Request,
                            static query =>
                                (PageSize: query.Int("pageSize"), Cursor: query.Text("cursor"), IncludeTotal: query.Bool("includeTotal"))
                        );
                        if (await jobs.GetIdAsync(job, ct) is not { } found)
                        {
                            return RouteTable.NotFound("Job");
                        }

                        var page = await jobs.ListCheckpointsAsync(
                            new ListJobCheckpointsQuery(
                                JobLookup.ById(found.JobId),
                                paging.PageSize,
                                paging.Cursor,
                                paging.IncludeTotal ?? false
                            ),
                            ct
                        );
                        return Results.Json(
                            ListJobCheckpointsResponse.From(page, jobsOptions.Value.MaxInlinePayloadBytes),
                            DashboardJsonContext.Default.ListJobCheckpointsResponse
                        );
                    })
            )
            .WithSummary("List one job's checkpoints.")
            .Produces<ListJobCheckpointsResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithQueryParameters([.. QueryParameterDocExtensions.PagingCore, .. QueryParameterDocExtensions.IncludeTotal]);
}
