using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore;

/// <summary>
/// GET /definitions/{jobNamespace}/{jobName}/input-template: the enqueue form's shape hint, from this host's manifest. A
/// job this host never registered is the 404, which the form reads as no template: a dashboard can point at a shared
/// ledger it has no job assembly for. A key that is no name is the 400, and no database is read, so the route answers
/// no 503.
/// </summary>
internal static class GetDefinitionInputTemplateEndpoint
{
    public static void Map(RouteGroupBuilder reads) =>
        reads
            .MapGet(
                "/definitions/{jobNamespace}/{jobName}/input-template",
                (string jobNamespace, string jobName, IActaOperations operations) =>
                    RouteTable.Run(() =>
                        Task.FromResult(
                            operations.Definitions.GetInputTemplate(jobNamespace, jobName) is { } template
                                ? Results.Json(template, ActaApiJsonContext.Default.GetDefinitionInputTemplateResponse)
                                : RouteTable.NotFound("Input template")
                        )
                    )
            )
            .WithMetadata(new ActaOperationAttribute("getDefinitionInputTemplate", ActaPermissions.Read))
            .WithSummary("Read a JSON template of the definition's input.")
            .Produces<GetDefinitionInputTemplateResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
}
