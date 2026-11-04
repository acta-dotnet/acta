using System.Text.Json.Serialization;

namespace Acta.AspNetCore;

/// <summary>One checkpoint of <see cref="GetJobDetailResponse"/>; its value is rendered as <see cref="JobPayloadBody"/>.</summary>
internal sealed record GetJobDetailCheckpoint(
    CheckpointKindCode Kind,
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CheckpointStatusCode? Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTime? DueAtUtc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JobPayloadBody? Value,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc
)
{
    public static GetJobDetailCheckpoint From(GetJobCheckpointsItem item, int maxInlineBytes) =>
        new(
            item.Kind,
            item.Name,
            item.Status,
            item.DueAtUtc,
            item.Value is { } value ? JobPayloadBody.From(value, maxInlineBytes) : null,
            item.CreatedAtUtc,
            item.ModifiedAtUtc
        );
}
