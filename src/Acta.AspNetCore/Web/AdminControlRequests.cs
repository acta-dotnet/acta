using Microsoft.AspNetCore.Http;

namespace Acta.AspNetCore.Web;

/// <summary>PATCH body for a tenant update. Null field clears it. ExpectedVersion drives the CAS.</summary>
internal sealed record TenantPatchRequest(
    string? DisplayName = null,
    string? Description = null,
    int? ExpectedVersion = null,
    string? ReasonMessage = null
);

/// <summary>
/// PATCH body for a namespace update. Null field clears it. ExpectedVersion drives the CAS.
/// </summary>
internal sealed record NamespacePatchRequest(
    string? OwnerTeam = null,
    string? Description = null,
    int? ExpectedVersion = null,
    string? ReasonMessage = null
);

/// <summary>HTTP projection of an admin control transition.</summary>
internal sealed record AdminControlResponse(AdminControlAction Action, int? Version);

/// <summary>Maps an <see cref="AdminControlResult"/> to an HTTP result: Applied/AlreadyInState 200, NotFound 404, VersionConflict 409.</summary>
internal static class AdminControlHttp
{
    public static IResult ToResult(AdminControlResult result) =>
        result.Action switch
        {
            AdminControlAction.Applied or AdminControlAction.AlreadyInState => Results.Json(
                new AdminControlResponse(result.Action, result.Version),
                DashboardJsonContext.Default.AdminControlResponse
            ),
            AdminControlAction.NotFound => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found."),
            AdminControlAction.VersionConflict => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Version conflict.",
                detail: "The expected version did not match the current row; re-read and retry."
            ),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Unknown admin outcome."),
        };
}
