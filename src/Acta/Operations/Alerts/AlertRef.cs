using System.Text.Json;
using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// Public stable reference for one alert row: "alr_" plus 26 lowercase Crockford Base32
/// characters encoding the canonical big-endian UUID bytes. Dashboards, HTTP APIs, and alert
/// transports address alerts by this value; the numeric alert id stays the internal engine
/// identity. Parsing is case-insensitive; emission is always canonical lowercase. Deduplication
/// never re-mints it: a condition that re-fires while its incident is open keeps that incident's ref.
/// </summary>
[JsonConverter(typeof(AlertRefJsonConverter))]
public readonly record struct AlertRef(Guid Value)
{
    public const string Prefix = "alr_";

    /// <summary>
    /// Allocate a fresh ref as a UUIDv7. The raise path mints each alert's ref in C# and passes
    /// it into the upsert, which applies it only when inserting; the database never defaults it.
    /// </summary>
    public static AlertRef New() => new(Guid.CreateVersion7());

    public override string ToString() => EntityRefCodec.Render(Prefix, Value);

    /// <summary>
    /// Parse an alert ref, throwing <see cref="FormatException"/> on malformed input.
    /// </summary>
    public static AlertRef Parse(string value) =>
        TryParse(value, out var alertRef) ? alertRef : throw new FormatException($"'{value}' is not a valid alert ref.");

    /// <summary>
    /// Parse an alert ref: <c>alr_</c> plus its Crockford payload, in any casing and with the o/i/l
    /// aliases, or the alert's Guid written plainly, with the prefix or without it. Rejects malformed values,
    /// another entity's prefix included.
    /// </summary>
    public static bool TryParse(string? value, out AlertRef alertRef)
    {
        if (EntityRefCodec.TryParse(Prefix, value, out var parsed))
        {
            alertRef = new AlertRef(parsed);
            return true;
        }

        alertRef = default;
        return false;
    }
}

/// <summary>
/// Serializes <see cref="AlertRef"/> as its canonical lowercase string form.
/// </summary>
public sealed class AlertRefJsonConverter : JsonConverter<AlertRef>
{
    public override AlertRef Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        AlertRef.TryParse(reader.GetString(), out var alertRef) ? alertRef : throw new JsonException("Invalid alert ref.");

    public override void Write(Utf8JsonWriter writer, AlertRef value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
