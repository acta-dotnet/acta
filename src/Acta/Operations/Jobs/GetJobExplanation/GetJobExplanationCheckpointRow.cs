namespace Acta;

/// <summary>One <c>checkpoints</c> row of any kind for the explain read, in SELECT order.</summary>
[DbProjection]
internal sealed record GetJobExplanationCheckpointRow(
    CheckpointKindCode Kind,
    string CheckpointName,
    CheckpointStatusCode? Status,
    DateTime? DueAtUtc
);
