using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// Public stable reference for one Job row: "job_" plus the Guid's 32 lowercase hex digits, such as
/// <c>job_01a14f4e954e7bf68ac609f3fcad8e3d</c>. Dashboards, HTTP APIs, and clients address jobs by this
/// value; the numeric JobId stays the internal engine identity. It copies and searches as one word,
/// and a UUIDv7 reads in creation order.
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
    /// Parse a job ref: <c>job_</c> exactly, then 32 hex digits in any casing. Rejects every other value,
    /// another entity's prefix, the dashed Guid, and the bare Guid included.
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
