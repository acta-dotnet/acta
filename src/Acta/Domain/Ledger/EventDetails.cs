using System.Text.Encodings.Web;
using System.Text.Json;

namespace Acta;

/// <summary>
/// An event's <c>details</c> payload as Acta writes it: one flat JSON object of named text facts (<c>actor</c>,
/// <c>reason</c>, <c>schedule</c>, and the like), stored with the JSON format id. A fact with no value is left out,
/// and an event with no facts stores no payload at all.
/// </summary>
internal static class EventDetails
{
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The format id a payload from <see cref="Write"/> is stored with: JSON, or none when there is no payload.</summary>
    public static byte FormatId(byte[]? details) => details is null ? JobPayloadFormat.None.Id : JobPayloadFormat.Json.Id;

    /// <summary>An instant as a detail fact: UTC at millisecond precision, the way a decision's range is recorded.</summary>
    public static string Instant(DateTime value) =>
        DbParams.ToUtc(value).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The facts as UTF-8 JSON, or null when every value is null.</summary>
    public static byte[]? Write(params ReadOnlySpan<(string Name, string? Value)> facts)
    {
        var any = false;
        foreach (var (_, value) in facts)
        {
            any |= value is not null;
        }
        if (!any)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, WriterOptions))
        {
            json.WriteStartObject();
            foreach (var (name, value) in facts)
            {
                if (value is not null)
                {
                    json.WriteString(name, value);
                }
            }
            json.WriteEndObject();
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// The named text fact of a JSON payload, or null when the payload is absent, not JSON, not an object, or
    /// lacks a text value under that name.
    /// </summary>
    public static string? Read(byte formatId, byte[]? details, string name)
    {
        if (details is null || formatId != JobPayloadFormat.Json.Id)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(details);
            return
                document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
