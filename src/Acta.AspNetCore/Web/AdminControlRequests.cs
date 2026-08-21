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

/// <summary>
/// Maps an <see cref="AdminControlResult"/> to an HTTP result: Applied/AlreadyInState 200, NotFound
/// 404, VersionConflict 409. All three carry the same <see cref="AdminControlResponse"/> body, so a
/// client reads <c>action</c> without special-casing the status code, and a version conflict carries
/// the row's current <c>version</c> so the caller retries without a re-read. Only an outcome outside
/// the enum is a fault, and a fault is the one thing this answers as a problem document.
/// </summary>
internal static class AdminControlHttp
{
    public static IResult ToResult(AdminControlResult result) =>
        result.Action switch
        {
            AdminControlAction.Applied or AdminControlAction.AlreadyInState => Json(result, StatusCodes.Status200OK),
            AdminControlAction.NotFound => Json(result, StatusCodes.Status404NotFound),
            AdminControlAction.VersionConflict => Json(result, StatusCodes.Status409Conflict),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Unknown admin outcome."),
        };

    private static IResult Json(AdminControlResult result, int statusCode) =>
        Results.Json(
            new AdminControlResponse(result.Action, result.Version),
            DashboardJsonContext.Default.AdminControlResponse,
            statusCode: statusCode
        );
}
