namespace Acta;

/// <summary>One <c>checkpoints</c> row of the focused Job for the lineage read, in SELECT order; a child latch carries its child's ref and name.</summary>
[DbProjection]
internal sealed record GetJobLineageMapCheckpointRow(
    CheckpointKindCode Kind,
    string CheckpointName,
    CheckpointStatusCode? Status,
    DateTime? DueAtUtc,
    JobRef? ChildJobRef = null,
    string? ChildName = null
);
