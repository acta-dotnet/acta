namespace Acta.Runtime;

/// <summary>The five result sets the lineage read returns for one focused Job; the job nodes bind the public models.</summary>
internal sealed record GetJobLineageMapRows(
    GetJobLineageMapNode Focus,
    IReadOnlyList<GetJobLineageMapNode> Ancestors,
    IReadOnlyList<GetJobLineageMapStepRow> Steps,
    IReadOnlyList<GetJobLineageMapCheckpointRow> Checkpoints,
    IReadOnlyList<GetJobLineageMapChild> Children
);
