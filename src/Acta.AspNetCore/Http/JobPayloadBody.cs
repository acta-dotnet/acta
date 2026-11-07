using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Acta.AspNetCore;

/// <summary>
/// A payload inside a JSON body, in a request and in a response: a JSON payload is <see cref="Json"/>, the value itself,
/// and any other format is <see cref="Base64"/> with its <see cref="FormatId"/>. A response also names the format, and
/// carries <see cref="ByteLength"/> and <see cref="Truncated"/> in place of the body past
/// <c>JobsOptions.MaxInlinePayloadBytes</c>, or <see cref="Base64"/> and <see cref="Malformed"/> for stored JSON that is
/// not one strict JSON value. No envelope is no payload.
/// </summary>
/// <param name="Json">A JSON payload, embedded as the JSON value itself.</param>
/// <param name="Base64">A payload in any other format, base64-encoded.</param>
/// <param name="FormatId">The payload format's id; required beside base64 in a request, and always present in a response.</param>
/// <param name="FormatName">The payload format's name; responses only.</param>
/// <param name="ByteLength">The stored payload's size in bytes, sent when its body is withheld; responses only.</param>
/// <param name="Truncated">True when the payload is larger than the inline limit and its body is withheld; read it whole from the job's
/// input or result route. Responses only.</param>
/// <param name="Malformed">True when a stored JSON payload is not valid JSON and is sent as base64 instead; responses only.</param>
internal sealed record JobPayloadBody(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(EmbeddedJsonConverter))]
        JsonElement? Json = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Base64 = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] byte? FormatId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FormatName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ByteLength = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Truncated = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Malformed = null
)
{
    /// <summary>The envelope a response carries for <paramref name="payload"/>, or null when there is none.</summary>
    public static JobPayloadBody? From(JobPayload? payload, int maxInlineBytes)
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
            return new JobPayloadBody(FormatId: id, FormatName: name, ByteLength: stored.Data.Length, Truncated: true);
        }

        if (id != JobPayloadFormat.Json.Id)
        {
            return new JobPayloadBody(Base64: Convert.ToBase64String(stored.Data.Span), FormatId: id, FormatName: name);
        }

        // A stored row that is not strict JSON cannot be embedded, so it travels as base64 instead of failing the read.
        try
        {
            _ = JobPayload.CopyBytes(stored.Format, stored.Data.Span);
            using var document = JsonDocument.Parse(stored.Data, new JsonDocumentOptions { MaxDepth = stored.Data.Length });
            return new JobPayloadBody(Json: document.RootElement.Clone(), FormatId: id, FormatName: name);
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            return new JobPayloadBody(Base64: Convert.ToBase64String(stored.Data.Span), FormatId: id, FormatName: name, Malformed: true);
        }
    }

    /// <summary>
    /// The payload a request's envelope at <paramref name="member"/> carries. It refuses, with the member at fault, a
    /// response-only member, other than exactly one of json and base64, a formatId beside json other than JSON's, and
    /// base64 that does not decode or lacks a binary formatId: 2 for bytes, or 128 to 255.
    /// </summary>
    /// <exception cref="InvalidControlException">The envelope breaks one of those rules.</exception>
    public JobPayload ToPayload(string member)
    {
        foreach (var (name, sent) in ResponseOnly())
        {
            if (sent)
            {
                throw Invalid($"{member}.{name} is read-only: a response carries it and a request does not.");
            }
        }

        if (Json.HasValue == (Base64 is not null))
        {
            throw Invalid($"{member} takes exactly one of json and base64.");
        }

        if (Json is { } json)
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

    private (string Name, bool Sent)[] ResponseOnly() =>
        [
            ("formatName", FormatName is not null),
            ("byteLength", ByteLength is not null),
            ("truncated", Truncated is not null),
            ("malformed", Malformed is not null),
        ];

    private static InvalidControlException Invalid(string detail) => new("Invalid request.", detail);
}

/// <summary>
/// Reads an embedded JSON value as any value and writes it back as its exact source text, so a stored payload reaches
/// the wire byte for byte, every digit and every character as stored.
/// </summary>
internal sealed class EmbeddedJsonConverter : JsonConverter<JsonElement?>
{
    public override JsonElement? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonElement.ParseValue(ref reader);

    public override void Write(Utf8JsonWriter writer, JsonElement? value, JsonSerializerOptions options)
    {
        if (value is { } element)
        {
            writer.WriteRawValue(JsonMarshal.GetRawUtf8Value(element), skipInputValidation: true);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
