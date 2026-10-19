using System.Text.Json;
using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// Serializes <see cref="JobRef"/> as its canonical lowercase string form.
/// </summary>
public sealed class JobRefJsonConverter : JsonConverter<JobRef>
{
    public override JobRef Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JobRef.TryParse(reader.GetString(), out var jobRef) ? jobRef : throw new JsonException("Invalid job ref.");

    public override void Write(Utf8JsonWriter writer, JobRef value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
