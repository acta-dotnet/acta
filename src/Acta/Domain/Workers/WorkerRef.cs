using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// Public stable reference for one worker registration: "wrk_" plus the Guid's 32 lowercase hex digits,
/// such as <c>wrk_01a14f4d7b2a7c4f8e1d3b5a6c7e9f01</c>. Dashboards, HTTP APIs, and event projections
/// address workers by this value; the numeric worker id stays the internal engine identity. A worker
/// row is never reused, so each registration mints a fresh ref for its lifetime.
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
