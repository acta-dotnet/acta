namespace Acta;

/// <summary>The three consistent result sets the explain read returns for one Job.</summary>
internal sealed record GetJobExplanationRows(
    GetJobExplanationHeaderRow Header,
    IReadOnlyList<GetJobExplanationStepRow> Steps,
    IReadOnlyList<GetJobExplanationCheckpointRow> Checkpoints
);
