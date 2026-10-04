using Microsoft.AspNetCore.Http;

namespace Acta.AspNetCore;

/// <summary>The body of a control verb; every one may carry the operator's reason.</summary>
internal interface IControlRequest
{
    string? ReasonMessage { get; }
}

/// <summary>One control request as a route table's verb sees it: the parsed body, its trimmed and capped reason, and the operator.</summary>
internal sealed record ControlCall<TBody>(
    HttpContext Http,
    IActaOperations Operations,
    TBody? Body,
    string? ReasonMessage,
    CancellationToken Ct
)
    where TBody : class
{
    /// <summary>The authenticated principal, stamped as the operator; never read from the body.</summary>
    public string? ActorKey => Http.User?.Identity?.Name;
}

/// <summary>A verb's own check on its body failed: a 400 under the verb's title, with this message as the detail.</summary>
internal sealed class InvalidControlException(string title, string detail) : Exception(detail)
{
    public string Title { get; } = title;
}

/// <summary>How a family's control outcome maps to a status code, and the codes its routes declare.</summary>
internal sealed record ControlStatus<TResponse>(Func<TResponse, int> Map, int[] Codes);

internal static class ControlStatus
{
    /// <summary>The admin verbs: Applied and AlreadyInState 200, NotFound 404, VersionConflict 409.</summary>
    public static ControlStatus<T> Admin<T>(params int[] codes)
        where T : IControlResponse =>
        new(
            static response =>
                response.Action switch
                {
                    ControlAction.Applied or ControlAction.AlreadyInState => StatusCodes.Status200OK,
                    ControlAction.NotFound => StatusCodes.Status404NotFound,
                    ControlAction.VersionConflict => StatusCodes.Status409Conflict,
                    _ => StatusCodes.Status500InternalServerError,
                },
            codes
        );

    /// <summary>The alert verbs, idempotent and never conflicting: NotFound 404, every other outcome 200.</summary>
    public static ControlStatus<T> Alert<T>()
        where T : IAlertControlResponse =>
        new(
            static response => response.Action == ControlAction.NotFound ? StatusCodes.Status404NotFound : StatusCodes.Status200OK,
            [StatusCodes.Status200OK, StatusCodes.Status404NotFound]
        );

    /// <summary>
    /// The outbox verbs park a command the next relay pass applies: Accepted 202, Rejected 409 (a command is already
    /// parked), NotFound 404 (no relay slot for the namespace).
    /// </summary>
    public static ControlStatus<T> Outbox<T>()
        where T : IOutboxControlResponse =>
        new(
            static response =>
                response.Action switch
                {
                    ControlAction.Accepted => StatusCodes.Status202Accepted,
                    ControlAction.Rejected => StatusCodes.Status409Conflict,
                    _ => StatusCodes.Status404NotFound,
                },
            [StatusCodes.Status202Accepted, StatusCodes.Status409Conflict, StatusCodes.Status404NotFound]
        );
}
