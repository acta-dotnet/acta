using Microsoft.AspNetCore.Http;

namespace Acta.AspNetCore.Web;

/// <summary>
/// The one answer every ref-addressed route gives a route segment that does not parse.
/// </summary>
/// <remarks>
/// A ref is caller input, so a malformed one is a 400 like any other malformed input, and that
/// leaves 404 meaning the single thing it can mean: a well-formed ref that names no row. Reads and
/// mutations answer alike, and every family answers alike, so a client reading a 404 knows the
/// target was addressable and simply is not there - which is what lets a control family carry its
/// envelope on 404 without a second body shape hiding behind the same code. The ref-valued query
/// parameters already answered this way; this is the same rule for the path.
/// </remarks>
internal static class RefSegment
{
    public static IResult Malformed(string parameterName, string entity) =>
        Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid request.",
            detail: $"{parameterName} is not a valid {entity} ref."
        );
}
