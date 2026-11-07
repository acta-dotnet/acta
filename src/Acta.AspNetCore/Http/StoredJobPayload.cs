using System.Text.Json;
using System.Text.Json.Serialization;

namespace Acta.AspNetCore;

/// <summary>
/// A stored payload as a response carries it inside a JSON body: its format, and its body as <see cref="Json"/> for a
/// JSON payload or <see cref="Base64"/> for any other format. Past the inline limit the body is
/// withheld, and <see cref="ByteLength"/> and <see cref="Truncated"/> stand in its place. Where there is no payload, the member that would carry this is absent.
/// </summary>
/// <param name="FormatId">The payload format's id: 1 for JSON, 2 for bytes, 128 to 255 for an application's own.</param>
/// <param name="FormatName">The payload format's name, such as json or bytes.</param>
/// <param name="Json">A JSON payload, embedded as the JSON value itself: any JSON value, null included.</param>
/// <param name="Base64">A payload in any other format, base64-encoded.</param>
/// <param name="ByteLength">The stored payload's size in bytes, sent when its body is withheld.</param>
/// <param name="Truncated">True when the payload is larger than the inline limit and its body is withheld. A job's input and result are
/// read whole from their own routes.</param>
/// <param name="Malformed">True when a stored JSON payload is not one valid JSON value and is sent as base64 instead.</param>
internal sealed record StoredJobPayload(
    byte FormatId,
    string FormatName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(EmbeddedJsonConverter))]
        JsonElement? Json = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Base64 = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ByteLength = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Truncated = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Malformed = null
)
{
    /// <summary>What a response carries for <paramref name="payload"/>, or null when there is none.</summary>
    public static StoredJobPayload? From(JobPayload? payload, int maxInlineBytes)
    {
        if (payload is not { IsNone: false } stored)
        {
            return null;
        }

        var (id, name) = (stored.Format.Id, stored.Format.Name);

        // Size is the only payload-read guard: past the inline cap the body is withheld, so an oversized row is never
        // materialized to the wire.
        if (stored.Data.Length > maxInlineBytes)
        {
            return new StoredJobPayload(id, name, ByteLength: stored.Data.Length, Truncated: true);
        }

        if (id != JobPayloadFormat.Json.Id)
        {
            return new StoredJobPayload(id, name, Base64: Convert.ToBase64String(stored.Data.Span));
        }

        // A stored row that is not strict JSON cannot be embedded, so it travels as base64 instead of failing the read.
        try
        {
            _ = JobPayload.CopyBytes(stored.Format, stored.Data.Span);
            using var document = JsonDocument.Parse(stored.Data, new JsonDocumentOptions { MaxDepth = stored.Data.Length });
            return new StoredJobPayload(id, name, Json: document.RootElement.Clone());
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            return new StoredJobPayload(id, name, Base64: Convert.ToBase64String(stored.Data.Span), Malformed: true);
        }
    }
}
