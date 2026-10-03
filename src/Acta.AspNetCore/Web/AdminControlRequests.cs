using Microsoft.AspNetCore.Http;

namespace Acta.AspNetCore;

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

/// <summary>
/// Maps an <see cref="AdminControlResult"/> to an HTTP result: Applied/AlreadyInState 200, NotFound
/// 404, VersionConflict 409. All three carry the result itself as the body, so a
/// client reads <c>action</c> without special-casing the status code, and a version conflict carries
/// the row's current <c>version</c> so the caller retries without a re-read. Only an outcome outside
/// the enum is a fault, and a fault is the one thing this answers as a problem document.
/// </summary>
internal static class AdminControlHttp
{
    public static IResult ToResult(AdminControlResult result) =>
        result.Action switch
        {
            ControlAction.Applied or ControlAction.AlreadyInState => Json(result, StatusCodes.Status200OK),
            ControlAction.NotFound => Json(result, StatusCodes.Status404NotFound),
            ControlAction.VersionConflict => Json(result, StatusCodes.Status409Conflict),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Unknown admin outcome."),
        };

    private static IResult Json(AdminControlResult result, int statusCode) =>
        Results.Json(result, DashboardJsonContext.Default.AdminControlResult, statusCode: statusCode);
}
