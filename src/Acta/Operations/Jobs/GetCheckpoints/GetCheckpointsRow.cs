namespace Acta;

/// <summary>
/// Flat <c>checkpoints</c> row for the job read surface, in SELECT order; value bytes reconstitute a
/// <see cref="JobPayload"/> after binding (None format id / null value means no payload).
/// </summary>
[DbProjection]
internal sealed record GetCheckpointsRow(
    CheckpointKindCode Kind,
    string CheckpointName,
    CheckpointStatusCode? Status,
    DateTime? DueAtUtc,
    byte ValueFormatId,
    byte[]? Value,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc
)
{
    public GetCheckpointsItem ToItem() =>
        new(
            Kind,
            CheckpointName,
            Status,
            DueAtUtc,
            ValueFormatId == 0 ? null : JobPayload.Trusted(JobPayloadFormat.ForId(ValueFormatId), Value ?? []),
            CreatedAtUtc,
            ModifiedAtUtc
        );
}
