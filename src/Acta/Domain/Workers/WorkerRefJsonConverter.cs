using System.Text.Json;
using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// Serializes <see cref="WorkerRef"/> as its canonical lowercase string form.
/// </summary>
public sealed class WorkerRefJsonConverter : JsonConverter<WorkerRef>
{
    public override WorkerRef Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        WorkerRef.TryParse(reader.GetString(), out var workerRef) ? workerRef : throw new JsonException("Invalid worker ref.");

    public override void Write(Utf8JsonWriter writer, WorkerRef value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
