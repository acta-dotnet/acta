using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// A worker's public reference: wrk_ followed by the 32 lowercase hex digits of a UUID, such as wrk_01a14f4d7b2a7c4f8e1d3b5a6c7e9f01.
/// Each start of a worker process gets a new one.
/// </summary>
/// <remarks>The numeric worker id stays the engine's internal identity; a worker row is never reused.</remarks>
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
    /// Parse a worker ref: <c>wrk_</c> exactly, then 32 hex digits in any casing. Rejects every other value,
    /// another entity's prefix, the dashed Guid, and the bare Guid included.
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
