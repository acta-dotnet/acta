namespace Acta;

/// <summary>
/// Flat <c>checkpoints</c> row for the job read surface, in SELECT order. <see cref="KindId"/> stays the stored byte
/// because it is the cursor's key: a kind this build does not know decodes to id 0, and a cursor built from that would
/// read the same rows again.
/// </summary>
[DbProjection]
internal sealed record ListJobCheckpointsRow(
    byte KindId,
    string CheckpointName,
    CheckpointStatusCode? Status,
    DateTime? DueAtUtc,
    byte ValueFormatId,
    long? ValueByteLength,
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
            ValueFormatId,
            ValueByteLength,
            CreatedAtUtc,
            ModifiedAtUtc
        );
}
