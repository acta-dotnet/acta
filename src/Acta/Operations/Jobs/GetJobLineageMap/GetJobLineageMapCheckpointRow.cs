namespace Acta;

/// <summary>One <c>checkpoints</c> row of the focused Job for the lineage read, in SELECT order.</summary>
[DbProjection]
internal sealed record GetJobLineageMapCheckpointRow(
    CheckpointKindCode Kind,
    string Name,
    CheckpointStatusCode? Status,
    DateTime? DueAtUtc
);
