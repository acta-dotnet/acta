using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;

namespace Acta.AspNetCore.Web;

/// <summary>
/// Shared request validation for the control endpoints: the anti-accident confirmation header, a
/// JSON-only body, and reason-message normalization (trim, empty becomes null, length cap). The
/// route's job ref is parsed before this runs.
/// </summary>
internal static class ControlEndpointValidation
{
    public static Task<(string? ReasonMessage, IResult? Error)> ReadAsync(
        HttpContext http,
        ActaEndpointOptions options,
        CancellationToken ct
    ) => ReadOptionalTextAsync(http, options, DashboardJsonContext.Default.JobControlRequest, static r => r.ReasonMessage, ct);

    /// <summary>
    /// Reads an optional JSON body and extracts one caller-supplied text field via
    /// <paramref name="selectText"/> (e.g. <c>JobControlRequest.ReasonMessage</c>, <c>AlertControlRequest.Note</c>):
    /// no body at all is a no-op (null, null); a present body is parsed and shape-validated (415/400 on
    /// malformed input), then the selected text is trimmed and length-capped via
    /// <see cref="ValidateReasonLength"/>. Shared by every control endpoint whose body carries a single
    /// optional free-text field.
    /// </summary>
    public static async Task<(string? Text, IResult? Error)> ReadOptionalTextAsync<T>(
        HttpContext http,
        ActaEndpointOptions options,
        JsonTypeInfo<T> typeInfo,
        Func<T, string?> selectText,
        CancellationToken ct
    )
        where T : class
    {
        if (CheckConfirmation(http, options) is { } confirmationError)
        {
            return (null, confirmationError);
        }

        T? request = null;
        if (http.Request.ContentLength is > 0 || !string.IsNullOrEmpty(http.Request.ContentType))
        {
            if (!http.Request.HasJsonContentType())
            {
                return (
                    null,
                    Problem(
                        StatusCodes.Status415UnsupportedMediaType,
                        "Unsupported content type.",
                        "Control requests with a body must send application/json."
                    )
                );
            }

            try
            {
                request = await http.Request.ReadFromJsonAsync(typeInfo, ct);
            }
            catch (JsonException)
            {
                return (
                    null,
                    Problem(StatusCodes.Status400BadRequest, "Invalid request body.", "The control request body is not valid JSON.")
                );
            }
        }

        var text = (request is null ? null : selectText(request))?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return (null, null);
        }

        return ValidateReasonLength(text, options) is { } lengthError ? (null, lengthError) : (text, null);
    }

    /// <summary>
    /// The reason-message length cap shared by every control endpoint. Returns a 400 problem result
    /// when the (already-trimmed) reason exceeds <see cref="ActaEndpointOptions.MaxReasonMessageLength"/>,
    /// else null.
    /// </summary>
    internal static IResult? ValidateReasonLength(string reason, ActaEndpointOptions options) =>
        reason.Length > options.MaxReasonMessageLength
            ? Problem(
                StatusCodes.Status400BadRequest,
                "Reason message too long.",
                $"Reason messages are capped at {options.MaxReasonMessageLength} characters."
            )
            : null;

    /// <summary>Returns a field-specific 400 response when optional metadata exceeds its product limit.</summary>
    internal static IResult? ValidateMetadataLength(string? value, string fieldName, int maxLength) =>
        value is { Length: var length } && length > maxLength
            ? Problem(
                StatusCodes.Status400BadRequest,
                "Invalid metadata.",
                $"{fieldName} must not exceed {maxLength} characters ({length} given)."
            )
            : null;

    /// <summary>
    /// Reads a mandatory JSON request body: 415 when the content type isn't application/json, 400 when
    /// the body is malformed JSON, and 400 when the body is absent altogether. Shared by every control
    /// endpoint that addresses its target via the body rather than the route (schedules, definitions,
    /// tenants). The confirmation-header guard is a separate, prior step the caller runs itself.
    /// </summary>
    public static async Task<(T? Body, IResult? Error)> ReadJsonBodyAsync<T>(
        HttpContext http,
        JsonTypeInfo<T> typeInfo,
        CancellationToken ct
    )
        where T : class
    {
        if (!http.Request.HasJsonContentType())
        {
            return (
                null,
                Problem(
                    StatusCodes.Status415UnsupportedMediaType,
                    "Unsupported content type.",
                    "Control requests must send application/json."
                )
            );
        }

        T? body;
        try
        {
            body = await http.Request.ReadFromJsonAsync(typeInfo, ct);
        }
        catch (JsonException)
        {
            return (null, Problem(StatusCodes.Status400BadRequest, "Invalid request body.", "The control request body is not valid JSON."));
        }

        return body is null
            ? (null, Problem(StatusCodes.Status400BadRequest, "Missing request body.", "A control request requires a JSON body."))
            : (body, null);
    }

    /// <summary>
    /// Reads an optional JSON request body to raw bytes for pass-through payloads. Returns null bytes
    /// when no body is present (a presence-only request); a 415 problem when a body is present but not
    /// application/json. The caller wraps the bytes in whatever payload shape it needs.
    /// </summary>
    public static async Task<(byte[]? Bytes, IResult? Error)> ReadOptionalJsonBytesAsync(
        HttpContext http,
        int maxBytes,
        CancellationToken ct
    )
    {
        var hasBody = http.Request.ContentLength is > 0 || !string.IsNullOrEmpty(http.Request.ContentType);
        if (!hasBody)
        {
            return (null, null);
        }

        if (!http.Request.HasJsonContentType())
        {
            return (
                null,
                Problem(
                    StatusCodes.Status415UnsupportedMediaType,
                    "Unsupported content type.",
                    "Request bodies must be sent as application/json."
                )
            );
        }

        if (http.Request.ContentLength is { } contentLength && contentLength > maxBytes)
        {
            return (null, PayloadTooLarge(maxBytes));
        }

        using var buffer = new MemoryStream();
        var readBufferSize = maxBytes >= 16 * 1024 ? 16 * 1024 : maxBytes + 1;
        var readBuffer = new byte[readBufferSize];
        var total = 0;
        while (true)
        {
            var read = await http.Request.Body.ReadAsync(readBuffer, ct);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maxBytes)
            {
                return (null, PayloadTooLarge(maxBytes));
            }

            await buffer.WriteAsync(readBuffer.AsMemory(0, read), ct);
        }

        var bytes = buffer.ToArray();
        return (bytes.Length == 0 ? null : bytes, null);
    }

    /// <summary>
    /// The anti-accident confirmation-header guard shared by every control endpoint. Returns a 400
    /// problem result when the header is required and missing or mismatched, else null.
    /// </summary>
    internal static IResult? CheckConfirmation(HttpContext http, ActaEndpointOptions options)
    {
        if (!options.RequireControlConfirmationHeader)
        {
            return null;
        }

        var header = http.Request.Headers[options.ControlConfirmationHeaderName];
        if (header.Count == 0 || !string.Equals(header[0], options.ControlConfirmationHeaderValue, StringComparison.Ordinal))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Missing control confirmation header",
                $"Control endpoints require {options.ControlConfirmationHeaderName}: {options.ControlConfirmationHeaderValue}."
            );
        }

        return null;
    }

    internal static IResult Problem(int statusCode, string title, string detail) =>
        Results.Problem(statusCode: statusCode, title: title, detail: detail);

    private static IResult PayloadTooLarge(int maxBytes) =>
        Problem(StatusCodes.Status413PayloadTooLarge, "Signal value too large.", $"Signal JSON bodies are capped at {maxBytes} bytes.");
}
