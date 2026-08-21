using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore.Features.Alerts;

/// <summary>
/// POST alert-control endpoints: thin HTTP wrappers over the <see cref="IAlerts"/> acknowledge/resolve
/// verbs. Unlike the natural-key-addressed schedule verbs, an alert is addressed by its public ref. The
/// verbs are idempotent (re-acknowledge/re-resolve is Applied without mutation), so this layer maps
/// <see cref="ControlAction"/> to only 200 (applied) and 404 (not found), never 409.
/// </summary>
internal static class AlertControlEndpoints
{
    public static void Map(RouteGroupBuilder group, ActaEndpointOptions options)
    {
        MapVerb(
            group,
            options,
            "acknowledge",
            "Acknowledge the alert: an operator has seen it.",
            static (operations, alertRef, note, actorKey, ct) => operations.Alerts.AcknowledgeAsync(alertRef, note, actorKey, ct)
        );
        MapVerb(
            group,
            options,
            "resolve",
            "Resolve the alert: its cause is handled.",
            static (operations, alertRef, note, actorKey, ct) => operations.Alerts.ResolveAsync(alertRef, note, actorKey, ct)
        );
    }

    private static void MapVerb(
        RouteGroupBuilder group,
        ActaEndpointOptions options,
        string verb,
        string summary,
        Func<IActaOperations, AlertRef, string?, string?, CancellationToken, ValueTask<AlertControlResult>> invoke
    )
    {
        group
            .MapPost(
                "/alerts/{alertRef}/" + verb,
                async Task<IResult> (string alertRef, HttpContext http, IActaOperations operations, CancellationToken ct) =>
                {
                    if (!AlertRef.TryParse(alertRef, out var parsed))
                    {
                        return RefSegment.Malformed("alertRef", "alert");
                    }

                    var (note, error) = await ControlEndpointValidation.ReadOptionalTextAsync(
                        http,
                        options,
                        DashboardJsonContext.Default.AlertControlRequest,
                        static r => r.ReasonMessage,
                        ct
                    );
                    if (error is not null)
                    {
                        return error;
                    }

                    // Operator identity for the audit trail comes from the authenticated principal, never the
                    // body; the verb stamps actor = Operator.
                    var actorKey = http.User?.Identity?.Name;
                    var result = await invoke(operations, parsed, note, actorKey, ct);
                    return ToResult(result);
                }
            )
            // One declaration for acknowledge and resolve alike.
            .WithSummary(summary)
            // The body is read manually rather than bound, so the document only learns its shape from
            // this declaration; optional because a bare POST acknowledges with no note.
            .AcceptsJson<AlertControlRequest>(optional: true)
            // Applied and not-found carry the same body, so a client reads `action` without
            // special-casing the status code.
            .Produces<AlertControlResponse>(StatusCodes.Status200OK)
            .Produces<AlertControlResponse>(StatusCodes.Status404NotFound)
            // Both verbs read the optional note body, so both can refuse a non-JSON content type.
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);
    }

    private static IResult ToResult(AlertControlResult result)
    {
        var statusCode = result.Action == ControlAction.NotFound ? StatusCodes.Status404NotFound : StatusCodes.Status200OK;
        return Results.Json(
            new AlertControlResponse(result.AlertRef, result.Action, result.AcknowledgedAtUtc, result.ResolvedAtUtc),
            DashboardJsonContext.Default.AlertControlResponse,
            statusCode: statusCode
        );
    }
}
