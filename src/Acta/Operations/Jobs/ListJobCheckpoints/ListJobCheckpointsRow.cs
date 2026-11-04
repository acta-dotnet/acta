namespace Acta;

/// <summary>
/// Flat <c>checkpoints</c> row for the job read surface, in SELECT order. <see cref="KindId"/> stays the stored byte
/// because it is the cursor's key: a kind this build does not know decodes to id 0, and a cursor built from that would
/// read the same rows again. Value bytes reconstitute a <see cref="JobPayload"/> (None format id means no payload).
/// </summary>
[DbProjection]
internal sealed record ListJobCheckpointsRow(
    byte KindId,
    string CheckpointName,
    CheckpointStatusCode? Status,
    DateTime? DueAtUtc,
    byte ValueFormatId,
    byte[]? Value,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc
)
{
    public ListJobCheckpointsItem ToItem() =>
        new(
            CheckpointKindCode.FromId(KindId),
            CheckpointName,
            Status,
            DueAtUtc,
            ValueFormatId == 0 ? null : JobPayload.Trusted(JobPayloadFormat.ForId(ValueFormatId), Value ?? []),
            CreatedAtUtc,
            ModifiedAtUtc
        );
}
