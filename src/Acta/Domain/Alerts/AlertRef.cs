using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// An alert's public reference: alr_ followed by the 32 lowercase hex digits of a UUID, such as alr_01a14f5038c17d0e9b2f6a4c81d3e5f7. An
/// incident keeps its ref while it is open: a condition that fires again gets no new one.
/// </summary>
/// <remarks>The numeric alert id stays the engine's internal identity.</remarks>
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
    /// Parse an alert ref: <c>alr_</c> exactly, then 32 hex digits in any casing. Rejects every other value,
    /// another entity's prefix, the dashed Guid, and the bare Guid included.
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
