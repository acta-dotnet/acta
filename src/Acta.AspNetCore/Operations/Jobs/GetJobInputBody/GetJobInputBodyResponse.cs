using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Acta.AspNetCore;

/// <summary>
/// A <see cref="JobPayload"/> rendered for the wire: <see cref="FormatName"/> picks the body field, raw JSON, decoded
/// text, or base64 (bytes and custom formats), and <c>none</c> carries none. <see cref="FormatId"/> is always sent, so a
/// clone can round-trip a custom format. Payloads are never gated, only sized: past
/// <c>JobsOptions.MaxInlinePayloadBytes</c> only the format, <see cref="ByteLength"/>, and <see cref="Truncated"/>
/// travel, since a handler result may have been persisted past the cap.
/// </summary>
internal sealed record GetJobInputBodyResponse(
    string FormatName,
    byte FormatId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Json = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Base64 = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ByteLength = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Truncated = null
)
{
    public static GetJobInputBodyResponse From(JobPayload payload, int maxInlineBytes)
    {
        if (payload.IsNone)
        {
            return new GetJobInputBodyResponse(JobPayloadFormat.NoneName, 0);
        }

        var format = payload.Format;

        // Size is the only payload-read guard: past the inline cap the body is withheld and only the
        // format identity and byte length travel, so an oversized row is never materialized to the wire.
        if (payload.Data.Length > maxInlineBytes)
        {
            return new GetJobInputBodyResponse(format.Name, format.Id, ByteLength: payload.Data.Length, Truncated: true);
        }

        if (format.Id == JobPayloadFormat.Json.Id)
        {
            // Parse (not reflect) so the stored JSON re-emits verbatim without a reflection-based serializer.
            using var doc = JsonDocument.Parse(payload.Data);
            return new GetJobInputBodyResponse(format.Name, format.Id, Json: doc.RootElement.Clone());
        }

        return format.Id == JobPayloadFormat.Text.Id
            ? new GetJobInputBodyResponse(format.Name, format.Id, Text: Encoding.UTF8.GetString(payload.Data.Span))
            : new GetJobInputBodyResponse(format.Name, format.Id, Base64: Convert.ToBase64String(payload.Data.Span));
    }
}
