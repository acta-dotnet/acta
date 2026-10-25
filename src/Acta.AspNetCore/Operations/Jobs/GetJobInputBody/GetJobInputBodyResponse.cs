using System.Text;
using System.Text.Json.Serialization;

namespace Acta.AspNetCore;

/// <summary>
/// A <see cref="JobPayload"/> rendered for the wire: <see cref="FormatName"/> picks the body field, the raw JSON text
/// or base64 (bytes and custom formats), and <c>none</c> carries none. <see cref="FormatId"/> is always sent, so a
/// clone can round-trip a custom format. Payloads are never gated, only sized: past
/// <c>JobsOptions.MaxInlinePayloadBytes</c> only the format, <see cref="ByteLength"/>, and <see cref="Truncated"/>
/// travel, since a handler result may have been persisted past the cap.
/// </summary>
internal sealed record GetJobInputBodyResponse(
    string FormatName,
    byte FormatId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Json = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Base64 = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ByteLength = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Truncated = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Malformed = null
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

        if (format.Id != JobPayloadFormat.Json.Id)
        {
            return new GetJobInputBodyResponse(format.Name, format.Id, Base64: Convert.ToBase64String(payload.Data.Span));
        }

        // The text travels as a string and is never re-parsed, so a number past 2^53 reaches a browser exactly as
        // stored. A stored row that is not strict JSON travels as base64 with Malformed set instead of failing the read.
        try
        {
            _ = JobPayload.CopyBytes(format, payload.Data.Span);
        }
        catch (ArgumentException)
        {
            return new GetJobInputBodyResponse(format.Name, format.Id, Base64: Convert.ToBase64String(payload.Data.Span), Malformed: true);
        }
        return new GetJobInputBodyResponse(format.Name, format.Id, Json: Encoding.UTF8.GetString(payload.Data.Span));
    }
}
