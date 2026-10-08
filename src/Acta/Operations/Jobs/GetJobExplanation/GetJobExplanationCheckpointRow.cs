namespace Acta;

/// <summary>One <c>checkpoints</c> row for the explain read (signal / timer / variable / progress / child-latch), in SELECT order.</summary>
[DbProjection]
internal sealed record GetJobExplanationCheckpointRow(
    CheckpointKindCode Kind,
    string Name,
    CheckpointStatusCode? Status,
    DateTime? DueAtUtc
);
