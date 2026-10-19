using System.Text.Json;
using System.Text.Json.Serialization;

namespace Acta;

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
