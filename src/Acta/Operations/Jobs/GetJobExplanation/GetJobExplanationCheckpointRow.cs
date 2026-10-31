namespace Acta;

/// <summary>One <c>checkpoints</c> row of any kind for the explain read, bound by name from a row_kind 3 row.</summary>
[DbProjection(ByName = true)]
internal sealed record GetJobExplanationCheckpointRow(
    CheckpointKindCode CheckpointKind,
    string CheckpointName,
    CheckpointStatusCode? CheckpointStatus,
    DateTime? DueAtUtc
);
