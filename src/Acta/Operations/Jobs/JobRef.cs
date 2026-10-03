using System.Text.Json;
using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// Public stable reference for one Job row: "job_" plus 26 lowercase Crockford Base32 characters
/// encoding the canonical big-endian UUID bytes. Dashboards, HTTP APIs, and clients address jobs
/// by this value; the numeric JobId stays the internal engine identity. Parsing is
/// case-insensitive; emission is always canonical lowercase.
/// </summary>
[JsonConverter(typeof(JobRefJsonConverter))]
public readonly record struct JobRef(Guid Value)
{
    public const string Prefix = "job_";

    /// <summary>
    /// Allocate a fresh ref as a UUIDv7. The enqueue and recurring-slot operations call this to mint
    /// each job's ref in C# and pass it into the inserting routine; the database never defaults it.
    /// </summary>
    public static JobRef New() => new(Guid.CreateVersion7());

    public override string ToString() => EntityRefCodec.Render(Prefix, Value);

    /// <summary>
    /// Parse a job ref, throwing <see cref="FormatException"/> on malformed input.
    /// </summary>
    public static JobRef Parse(string value) =>
        TryParse(value, out var jobRef) ? jobRef : throw new FormatException($"'{value}' is not a valid job ref.");

    /// <summary>
    /// Parse a job ref. Accepts any input casing and the Crockford o/i/l aliases; rejects
    /// malformed values (another entity's prefix included) after normalization.
    /// </summary>
    public static bool TryParse(string? value, out JobRef jobRef)
    {
        if (EntityRefCodec.TryParse(Prefix, value, out var parsed))
        {
            jobRef = new JobRef(parsed);
            return true;
        }

        jobRef = default;
        return false;
    }
}

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
