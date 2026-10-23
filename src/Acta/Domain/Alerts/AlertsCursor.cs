using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// The <c>sys.alerts</c> projection cursor as one value: the <c>created_at_utc</c> ticks and the id
/// of the last event consumed, or the horizon a short batch advanced the instant to. One variable
/// holds both halves so a checkpoint is one write and two writers can never interleave the halves
/// of different checkpoints.
/// </summary>
internal sealed record AlertsCursor(long Ticks, long EventId)
{
    /// <summary>The variable on the namespace's <c>sys.alerts</c> slot that holds the cursor.</summary>
    public const string VariableName = "alerts-cursor";

    [JsonIgnore]
    public DateTime Utc => new(Ticks, DateTimeKind.Utc);
}
