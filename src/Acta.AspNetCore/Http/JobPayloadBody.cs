using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Acta.AspNetCore;

/// <summary>
/// A payload as a request carries it inside a JSON body: exactly one of <see cref="Json"/>, a JSON payload as the value
/// itself, and <see cref="Base64"/>, a payload in any other format beside its <see cref="FormatId"/>.
/// </summary>
/// <param name="Json">A JSON payload, embedded as the JSON value itself: any JSON value, null included. Beside base64 a null here
/// counts as left out.</param>
/// <param name="Base64">A payload in any other format, base64-encoded.</param>
/// <param name="FormatId">The payload format's id. Beside base64 it is required: 2 for bytes, or 128 to 255 for an application's own.
/// Beside json it is left out, or is 1.</param>
internal sealed record JobPayloadBody(
    [property: JsonConverter(typeof(EmbeddedJsonConverter))] JsonElement? Json = null,
    string? Base64 = null,
    byte? FormatId = null
)
{
    /// <summary>
    /// The payload the request's <paramref name="member"/> carries. It refuses, with the member at fault, other than
    /// exactly one of json and base64, a formatId beside json other than JSON's, and base64 that does not decode or lacks
    /// a binary formatId.
    /// </summary>
    /// <exception cref="InvalidControlException">The request breaks one of those rules.</exception>
    public JobPayload ToPayload(string member)
    {
        // A client that writes every member sends a null json beside its base64; that null is no payload.
        var sent = Json is { ValueKind: JsonValueKind.Null } && Base64 is not null ? null : Json;
        if (sent.HasValue == (Base64 is not null))
        {
            throw Invalid($"{member} takes exactly one of json and base64.");
        }

        if (sent is { } json)
        {
            if (FormatId is { } jsonFormat && jsonFormat != JobPayloadFormat.Json.Id)
            {
                throw Invalid($"{member}.formatId beside json must be {JobPayloadFormat.Json.Id}, the JSON format.");
            }

            try
            {
                return JobPayload.CopyBytes(JobPayloadFormat.Json, JsonMarshal.GetRawUtf8Value(json));
            }
            catch (ArgumentException ex)
            {
                throw Invalid($"{member}.json: {ex.Message}");
            }
        }

        if (FormatId is not { } formatId || (formatId != JobPayloadFormat.Bytes.Id && formatId < 128))
        {
            throw Invalid($"{member}.formatId beside base64 must be {JobPayloadFormat.Bytes.Id} for bytes, or 128 to 255.");
        }

        try
        {
            return JobPayload.FromBytes(JobPayloadFormat.ForId(formatId), Convert.FromBase64String(Base64!));
        }
        catch (FormatException)
        {
            throw Invalid($"{member}.base64 is not valid base64.");
        }
    }

    private static InvalidControlException Invalid(string detail) => new("Invalid request.", detail);
}
