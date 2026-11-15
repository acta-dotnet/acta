using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore;

/// <summary>
/// DELETE /jobs/{jobRef}/signals/{signalName}: clear a named signal from a finished job or a permanent job between
/// firings, the name read as the raise reads it.
/// </summary>
internal static class ClearJobSignalEndpoint
{
    public static void Map(RouteGroupBuilder controls) =>
        controls
            .MapDelete(
                "/jobs/{jobRef}/signals/{signalName}",
                async (string jobRef, string signalName, HttpContext http, IJobs jobs, CancellationToken ct) =>
                {
                    if (!JobRef.TryParse(jobRef, out var parsed))
                    {
                        return RefSegment.Malformed("jobRef", "job");
                    }

                    try
                    {
                        signalName = NameSyntax.Canonicalize(
                            signalName,
                            nameof(signalName),
                            NameSyntax.ExtendedMaxLength,
                            reserveSystem: true
                        );
                    }
                    catch (ArgumentException ex)
                    {
                        return ControlEndpointValidation.Problem(StatusCodes.Status400BadRequest, "Invalid signal name.", ex.Message);
                    }

                    // Operator identity for the audit trail comes from the authenticated principal.
                    var result = await jobs.ClearSignalAsync(JobLookup.ByRef(parsed), signalName, http.User?.Identity?.Name, ct);
                    return RouteTable.Control(result, ControlStatus.Job<ClearJobSignalResponse>());
                }
            )
            .WithMetadata(new ActaOperationAttribute("clearJobSignal", ActaPermissions.Control))
            .WithSummary("Clear a named signal from a finished job, so a restart waits for it again.")
            .Produces<ClearJobSignalResponse>(StatusCodes.Status200OK)
            .Produces<ClearJobSignalResponse>(StatusCodes.Status409Conflict)
            .Produces<ClearJobSignalResponse>(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
}
