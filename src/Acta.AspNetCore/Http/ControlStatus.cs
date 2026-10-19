using Microsoft.AspNetCore.Http;

namespace Acta.AspNetCore;

/// <summary>How a family's control outcome maps to a status code, and the codes its routes declare.</summary>
internal sealed record ControlStatus<TResponse>(Func<TResponse, int> Map, int[] Codes);

internal static class ControlStatus
{
    /// <summary>The job verbs: Applied 200, Rejected and VersionConflict 409 (the job's state or a stale expectedVersion), NotFound 404.</summary>
    public static ControlStatus<T> Job<T>()
        where T : IJobControlResponse =>
        new(
            static response =>
                response.Action switch
                {
                    ControlAction.Applied => StatusCodes.Status200OK,
                    ControlAction.Rejected or ControlAction.VersionConflict => StatusCodes.Status409Conflict,
                    _ => StatusCodes.Status404NotFound,
                },
            [StatusCodes.Status200OK, StatusCodes.Status409Conflict, StatusCodes.Status404NotFound]
        );

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

    /// <summary>The definition verbs: Applied 200, VersionConflict (a stale expectedVersion) 409, NotFound 404.</summary>
    public static ControlStatus<T> Definition<T>()
        where T : IDefinitionControlResponse =>
        new(
            static response =>
                response.Action switch
                {
                    ControlAction.Applied => StatusCodes.Status200OK,
                    ControlAction.VersionConflict => StatusCodes.Status409Conflict,
                    _ => StatusCodes.Status404NotFound,
                },
            [StatusCodes.Status200OK, StatusCodes.Status409Conflict, StatusCodes.Status404NotFound]
        );

    /// <summary>The schedule verbs: Applied 200, Rejected and VersionConflict 409 (the schedule's state or a stale expectedVersion), NotFound 404.</summary>
    public static ControlStatus<T> Schedule<T>()
        where T : IScheduleControlResponse =>
        new(
            static response =>
                response.Action switch
                {
                    ControlAction.Applied => StatusCodes.Status200OK,
                    ControlAction.Rejected or ControlAction.VersionConflict => StatusCodes.Status409Conflict,
                    _ => StatusCodes.Status404NotFound,
                },
            [StatusCodes.Status200OK, StatusCodes.Status409Conflict, StatusCodes.Status404NotFound]
        );
}
