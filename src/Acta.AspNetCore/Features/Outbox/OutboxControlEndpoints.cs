using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore.Features.Outbox;

/// <summary>
/// POST outbox-control endpoints: thin HTTP wrappers over the <see cref="IOutbox"/> requeue/discard
/// verbs. The source is addressed by its namespace in the route
/// (<c>/outbox/{jobNamespace}/…</c>) like every other namespace verb; the verbs park a durable
/// command the next relay pass applies, so this layer maps <see cref="ControlAction"/> to 202
/// (accepted), 409 (rejected: a pending command occupies the inbox), and 404 (no relay slot for the
/// namespace).
/// </summary>
internal static class OutboxControlEndpoints
{
    public static void Map(RouteGroupBuilder outer, ActaEndpointOptions options)
    {
        // The two verbs share one response shape across their three outcomes, so declare it once.
        var group = outer.MapGroup("");
        group.ProducesJson<OutboxControlResponse>(StatusCodes.Status202Accepted);
        group.ProducesJson<OutboxControlResponse>(StatusCodes.Status409Conflict);
        group.ProducesJson<OutboxControlResponse>(StatusCodes.Status404NotFound);
        // Both verbs read the optional body, so both can refuse a content type that is not JSON.
        group.ProducesProblem(StatusCodes.Status415UnsupportedMediaType);

        group
            .MapPost(
                "/outbox/{jobNamespace}/requeue",
                (string jobNamespace, HttpContext http, IActaOperations operations, CancellationToken ct) =>
                    HandleAsync(http, operations, options, "requeue", jobNamespace, ct)
            )
            // The body is read manually rather than bound, so the document only learns its shape from
            // this declaration; optional because a bare POST targets every quarantined row.
            .AcceptsJson<OutboxControlRequest>(optional: true)
            .WithSummary("Return quarantined outbox rows to pending; the next relay pass applies it.");

        group
            .MapPost(
                "/outbox/{jobNamespace}/discard",
                (string jobNamespace, HttpContext http, IActaOperations operations, CancellationToken ct) =>
                    HandleAsync(http, operations, options, "discard", jobNamespace, ct)
            )
            .AcceptsJson<OutboxControlRequest>(optional: true)
            .WithSummary("Delete quarantined outbox rows, keeping the ids as audit evidence.");
    }

    private static async Task<IResult> HandleAsync(
        HttpContext http,
        IActaOperations operations,
        ActaEndpointOptions options,
        string verb,
        string jobNamespace,
        CancellationToken ct
    )
    {
        // The body is optional: a bare POST targets every quarantined row with no reason.
        var (body, error) = await ControlEndpointValidation.ReadOptionalJsonBodyAsync(
            http,
            options,
            DashboardJsonContext.Default.OutboxControlRequest,
            ct
        );
        if (error is not null)
        {
            return error;
        }

        var reasonMessage = body?.ReasonMessage?.Trim() is { Length: > 0 } trimmed ? trimmed : null;
        if (reasonMessage is not null && ControlEndpointValidation.ValidateReasonLength(reasonMessage, options) is { } lengthError)
        {
            return lengthError;
        }

        // Operator identity for the audit trail comes from the authenticated principal, never the
        // body; the applying tick stamps actor = Operator with this key.
        var actorKey = http.User?.Identity?.Name;
        try
        {
            var result =
                verb == "requeue"
                    ? await operations.Outbox.RequeueAsync(jobNamespace, body?.OutboxIds, reasonMessage, actorKey, ct)
                    : await operations.Outbox.DiscardAsync(jobNamespace, body?.OutboxIds, reasonMessage, actorKey, ct);
            return ToResult(verb, result);
        }
        catch (ArgumentException ex)
        {
            // Malformed namespace or an explicit empty id list; both are caller errors, not faults.
            return ControlEndpointValidation.Problem(StatusCodes.Status400BadRequest, "Invalid outbox command.", ex.Message);
        }
    }

    private static IResult ToResult(string verb, OutboxControlResult result)
    {
        var (statusCode, message) = result.Action switch
        {
            ControlAction.Accepted => (StatusCodes.Status202Accepted, $"Outbox {verb} accepted; the next relay pass applies it."),
            ControlAction.Rejected => (
                StatusCodes.Status409Conflict,
                $"Outbox {verb} rejected: a pending {verb} command is already parked for this source."
            ),
            _ => (StatusCodes.Status404NotFound, "No outbox relay slot exists for that namespace."),
        };

        return Results.Json(
            new OutboxControlResponse(result.Action, result.PendingSinceUtc, message),
            DashboardJsonContext.Default.OutboxControlResponse,
            statusCode: statusCode
        );
    }
}
