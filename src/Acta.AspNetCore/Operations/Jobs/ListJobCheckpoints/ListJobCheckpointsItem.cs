using System.Text.Json.Serialization;

namespace Acta.AspNetCore;

/// <summary>One checkpoint of a <see cref="ListJobCheckpointsResponse"/>; its value is rendered as <see cref="JobPayloadBody"/>.</summary>
/// <param name="Kind">The checkpoint's kind, such as variable, signal, or timer.</param>
/// <param name="Name">The checkpoint's name, unique within its job and kind.</param>
/// <param name="Status">The checkpoint's status; null for a kind that has none, such as a variable.</param>
/// <param name="DueAtUtc">When a bounded wait expires or a timer is due, in UTC; null otherwise.</param>
/// <param name="Value">The checkpoint's stored value as a payload envelope; absent when it holds none.</param>
/// <param name="CreatedAtUtc">When the row was created, in UTC.</param>
/// <param name="ModifiedAtUtc">When the row last changed, in UTC.</param>
internal sealed record ListJobCheckpointsItem(
    CheckpointKindCode Kind,
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CheckpointStatusCode? Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTime? DueAtUtc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JobPayloadBody? Value,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc
)
{
    public static ListJobCheckpointsItem From(Acta.ListJobCheckpointsItem item, int maxInlineBytes) =>
        new(
            item.Kind,
            item.Name,
            item.Status,
            item.DueAtUtc,
            JobPayloadBody.From(item.Value, maxInlineBytes),
            item.CreatedAtUtc,
            item.ModifiedAtUtc
        );
}
