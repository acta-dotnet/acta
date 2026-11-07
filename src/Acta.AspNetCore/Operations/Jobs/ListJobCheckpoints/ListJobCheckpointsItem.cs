using System.Text.Json.Serialization;

namespace Acta.AspNetCore;

/// <summary>One checkpoint of a <see cref="ListJobCheckpointsResponse"/>; its value is rendered as <see cref="JobPayloadBody"/>.</summary>
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
