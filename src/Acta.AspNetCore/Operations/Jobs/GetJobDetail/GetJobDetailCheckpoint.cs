using System.Text.Json.Serialization;

namespace Acta.AspNetCore;

/// <summary>One checkpoint of <see cref="GetJobDetailResponse"/>; its value is rendered as <see cref="GetJobInputBodyResponse"/>.</summary>
internal sealed record GetJobDetailCheckpoint(
    CheckpointKindCode Kind,
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CheckpointStatusCode? Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTime? DueAtUtc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GetJobInputBodyResponse? Value,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc
)
{
    public static GetJobDetailCheckpoint From(GetCheckpointsItem item, int maxInlineBytes) =>
        new(
            item.Kind,
            item.Name,
            item.Status,
            item.DueAtUtc,
            item.Value is { } value ? GetJobInputBodyResponse.From(value, maxInlineBytes) : null,
            item.CreatedAtUtc,
            item.ModifiedAtUtc
        );
}
