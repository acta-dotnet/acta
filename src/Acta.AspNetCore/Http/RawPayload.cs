using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Acta.AspNetCore;

/// <summary>
/// A payload served as the HTTP body itself, never truncated: a JSON payload as <c>application/json</c> with the stored
/// bytes, any other format, and stored JSON that is not one strict JSON value, as <c>application/octet-stream</c> with its
/// format id in <see cref="FormatHeader"/>, and no payload as 204.
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
            try
            {
                _ = JobPayload.CopyBytes(stored.Format, stored.Data.Span);
                return Results.Bytes(stored.Data, JsonContentType);
            }
            catch (ArgumentException)
            {
                // A stored row that is not strict JSON goes as bytes, so a client that parses by content type still reads it.
            }
        }

        http.Response.Headers[FormatHeader] = stored.Format.Id.ToString(CultureInfo.InvariantCulture);
        return Results.Bytes(stored.Data, BinaryContentType);
    }

    /// <summary>Declares a payload read: the payload in either content type, its format header, and the 204 for none.</summary>
    public static RouteHandlerBuilder ProducesPayload(this RouteHandlerBuilder route) =>
        route
            .Produces(StatusCodes.Status200OK, responseType: null, JsonContentType, BinaryContentType)
            .Produces(StatusCodes.Status204NoContent)
            .WithDescription(
                "A JSON payload is the body as application/json. Any other format is application/octet-stream with its format id in "
                    + $"{FormatHeader}, and so is a stored JSON payload that is not one strict JSON value, with format id 1."
            )
            .WithMetadata(
                new ResponseHeaderDoc(
                    FormatHeader,
                    "The payload's format id, on a payload served as application/octet-stream: 2 for bytes, 128 to 255 for an "
                        + "application's own, and 1 for stored JSON that is not one strict JSON value."
                )
            );
}
