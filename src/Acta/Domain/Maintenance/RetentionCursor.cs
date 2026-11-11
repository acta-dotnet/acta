using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// Where the jobs walk of <c>sys.retention</c> stands, as one value: the <c>created_at_utc</c> ticks and the id of the
/// job it reads before, in the order of <c>ix_jobs_namespace_created</c>. One variable holds both halves, so a pass
/// stores its place in one write. <see cref="None"/> is no place: the next pass starts a cycle.
/// </summary>
internal sealed record RetentionCursor(long Ticks, long JobId)
{
    /// <summary>The variable on the namespace's <c>sys.retention</c> slot that holds the cursor.</summary>
    public const string VariableName = "retention-cursor";

    public static readonly RetentionCursor None = new(0, 0);

    [JsonIgnore]
    public DateTime Utc => new(Ticks, DateTimeKind.Utc);
}
