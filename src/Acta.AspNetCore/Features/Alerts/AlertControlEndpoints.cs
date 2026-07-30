using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore.Features.Alerts;

/// <summary>
/// POST alert-control endpoints: thin HTTP wrappers over the <see cref="IAlerts"/> acknowledge/resolve
/// verbs. Unlike the natural-key-addressed schedule verbs, an alert is addressed by its route id. The
/// verbs are idempotent (re-acknowledge/re-resolve is Applied without mutation), so this layer maps
/// <see cref="JobControlAction"/> to only 200 (applied) and 404 (not found), never 409.
/// </summary>
internal static class AlertControlEndpoints
{
    public static void Map(RouteGroupBuilder group, ActaEndpointOptions options)
    {
        MapVerb(
            group,
            options,
            "acknowledge",
            static (operations, alertId, note, actorKey, ct) => operations.Alerts.AcknowledgeAsync(alertId, note, actorKey, ct)
        );
        MapVerb(
            group,
            options,
            "resolve",
            static (operations, alertId, note, actorKey, ct) => operations.Alerts.ResolveAsync(alertId, note, actorKey, ct)
        );
    }

    private static void MapVerb(
        RouteGroupBuilder group,
        ActaEndpointOptions options,
        string verb,
        Func<IActaOperations, long, string?, string?, CancellationToken, ValueTask<AlertControlResult>> invoke
    )
    {
        group.MapPost(
            "/alerts/{alertId:long}/" + verb,
            async Task<IResult> (long alertId, HttpContext http, IActaOperations operations, CancellationToken ct) =>
            {
                var (note, error) = await ControlEndpointValidation.ReadOptionalTextAsync(
                    http,
                    options,
                    DashboardJsonContext.Default.AlertControlRequest,
                    static r => r.Note,
                    ct
                );
                if (error is not null)
                {
                    return error;
                }

                // Operator identity for the audit trail comes from the authenticated principal, never the
                // body; the verb stamps actor = Operator.
                var actorKey = http.User?.Identity?.Name;
                var result = await invoke(operations, alertId, note, actorKey, ct);
                return ToResult(result);
            }
        );
    }

    private static IResult ToResult(AlertControlResult result)
    {
        var statusCode = result.Action == JobControlAction.NotFound ? StatusCodes.Status404NotFound : StatusCodes.Status200OK;
        return Results.Json(
            new AlertControlResponse(result.AlertId, result.Action, result.AcknowledgedAtUtc, result.ResolvedAtUtc),
            DashboardJsonContext.Default.AlertControlResponse,
            statusCode: statusCode
        );
    }
}
