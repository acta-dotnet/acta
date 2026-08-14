using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore.Features.Definitions;

/// <summary>
/// PATCH definition-control endpoint: a thin HTTP wrapper over <see cref="IDefinitions"/>. The
/// definition is addressed by its catalog id in the route; the JSON body carries the version (optimistic
/// concurrency), the full override set, and an optional note. The verb owns the version gate and audit
/// stamping; this layer validates the request shape and maps <see cref="ControlAction"/> to 200
/// (applied), 409 (version conflict), and 404 (not found). An invalid override (e.g. an out-of-range
/// numeric value or a malformed backoff expression) throws <see cref="ArgumentException"/>, caught here
/// and mapped to 400.
/// </summary>
internal static class DefinitionControlEndpoints
{
    public static void Map(RouteGroupBuilder group, ActaEndpointOptions options)
    {
        group
            .MapPatch(
                "/definitions/{defId:int}",
                async Task<IResult> (int defId, HttpContext http, IActaOperations operations, CancellationToken ct) =>
                {
                    if (ControlEndpointValidation.CheckConfirmation(http, options) is { } confirmationError)
                    {
                        return confirmationError;
                    }

                    var (body, error) = await ControlEndpointValidation.ReadJsonBodyAsync(
                        http,
                        DashboardJsonContext.Default.SetDefinitionOverridesRequest,
                        ct
                    );
                    if (error is not null)
                    {
                        return error;
                    }

                    // Operator identity for the audit trail comes from the authenticated principal, never the
                    // body; the verb stamps actor = Operator.
                    var actorKey = http.User?.Identity?.Name;
                    try
                    {
                        var result = await operations.Definitions.UpdateOverridesAsync(
                            defId,
                            body!.Version,
                            body.Overrides ?? new JobDefinitionPolicyOverrides(),
                            actorKey,
                            body.Note,
                            ct
                        );
                        return ToResult(defId, result);
                    }
                    catch (ArgumentException ex)
                    {
                        return ControlEndpointValidation.Problem(
                            StatusCodes.Status400BadRequest,
                            "Invalid definition overrides.",
                            ex.Message
                        );
                    }
                }
            )
            .Produces<DefinitionOverrideResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static IResult ToResult(int defId, DefinitionControlResult result)
    {
        var (statusCode, message) = result.Action switch
        {
            ControlAction.Applied => (StatusCodes.Status200OK, "Definition overrides applied."),
            ControlAction.Rejected => (
                StatusCodes.Status409Conflict,
                "Definition override rejected: the definition changed since you loaded it (version conflict). Reload and retry."
            ),
            _ => (StatusCodes.Status404NotFound, "Definition not found."),
        };

        return Results.Json(
            new DefinitionOverrideResponse(defId, result.Action, message),
            DashboardJsonContext.Default.DefinitionOverrideResponse,
            statusCode: statusCode
        );
    }
}
