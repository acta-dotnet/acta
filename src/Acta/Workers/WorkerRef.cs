using System.Text.Json;
using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// Public stable reference for one worker registration: "wrk_" plus 26 lowercase Crockford
/// Base32 characters encoding the canonical big-endian UUID bytes. Dashboards, HTTP APIs, and
/// event projections address workers by this value; the numeric worker id stays the internal
/// engine identity. Parsing is case-insensitive; emission is always canonical lowercase. A
/// worker row is never reused, so each registration mints a fresh ref for its lifetime.
/// </summary>
[JsonConverter(typeof(WorkerRefJsonConverter))]
public readonly record struct WorkerRef(Guid Value)
{
    public const string Prefix = "wrk_";

    /// <summary>
    /// Allocate a fresh ref as a UUIDv7. Worker registration mints the ref in C# and passes it
    /// into the inserting routine; the database never defaults it.
    /// </summary>
    public static WorkerRef New() => new(Guid.CreateVersion7());

    public override string ToString() => EntityRefCodec.Render(Prefix, Value);

    /// <summary>
    /// Parse a worker ref, throwing <see cref="FormatException"/> on malformed input.
    /// </summary>
    public static WorkerRef Parse(string value) =>
        TryParse(value, out var workerRef) ? workerRef : throw new FormatException($"'{value}' is not a valid worker ref.");

    /// <summary>
    /// Parse a worker ref. Accepts any input casing and the Crockford o/i/l aliases; rejects
    /// malformed values (another entity's prefix included) after normalization.
    /// </summary>
    public static bool TryParse(string? value, out WorkerRef workerRef)
    {
        if (EntityRefCodec.TryParse(Prefix, value, out var parsed))
        {
            workerRef = new WorkerRef(parsed);
            return true;
        }

        workerRef = default;
        return false;
    }
}

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
