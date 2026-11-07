using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Acta.AspNetCore;

/// <summary>
/// A payload served as the HTTP body itself, never truncated: a JSON payload as <c>application/json</c> with the stored
/// bytes, any other format as <c>application/octet-stream</c> with its format id in <see cref="FormatHeader"/>, and no
/// payload as 204.
/// </summary>
internal static class RawPayload
{
    public const string FormatHeader = "X-Acta-Payload-Format";

    public const string JsonContentType = "application/json";

    public const string BinaryContentType = "application/octet-stream";

    public static IResult Serve(HttpContext http, JobPayload? payload)
    {
        if (payload is not { IsNone: false } stored)
        {
            return Results.NoContent();
        }

        if (stored.Format.Id == JobPayloadFormat.Json.Id)
        {
            return Results.Bytes(stored.Data, JsonContentType);
        }

        http.Response.Headers[FormatHeader] = stored.Format.Id.ToString(CultureInfo.InvariantCulture);
        return Results.Bytes(stored.Data, BinaryContentType);
    }

    /// <summary>Declares a payload read: the payload in either content type, its format header, and the 204 for none.</summary>
    public static RouteHandlerBuilder ProducesPayload(this RouteHandlerBuilder route) =>
        route
            .Produces(StatusCodes.Status200OK, responseType: null, JsonContentType, BinaryContentType)
            .Produces(StatusCodes.Status204NoContent)
            .WithMetadata(
                new ResponseHeaderDoc(
                    FormatHeader,
                    "The payload's format id, on a payload in a format other than JSON: 2 for bytes, 128 to 255 for an application's own."
                )
            );
}
