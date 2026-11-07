using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Acta.AspNetCore;

/// <summary>
/// Reads an embedded JSON value as any value, a JSON null included, and writes it back as its exact source text, so a
/// stored payload reaches the wire byte for byte, every digit and every character as stored.
/// </summary>
internal sealed class EmbeddedJsonConverter : JsonConverter<JsonElement?>
{
    /// <summary>A null token is a value here: without this the member would read as absent.</summary>
    public override bool HandleNull => true;

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
