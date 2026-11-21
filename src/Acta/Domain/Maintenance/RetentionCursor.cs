using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// Where the jobs walk of <c>sys.retention</c> stands, as one value: the <c>retention_until_utc</c> ticks and the id of
/// the last job it examined, in the order of <c>ix_jobs_state_retention</c>. One variable holds both halves, so a pass
/// stores its place in one write. <see cref="None"/> is no place: the next pass starts at the oldest expired job.
/// </summary>
internal sealed record RetentionCursor(long Ticks, long JobId)
{
    /// <summary>The variable on the namespace's <c>sys.retention</c> slot that holds the cursor.</summary>
    public const string VariableName = "retention-cursor";

    public static readonly RetentionCursor None = new(0, 0);

    [JsonIgnore]
    public DateTime Utc => new(Ticks, DateTimeKind.Utc);
}
