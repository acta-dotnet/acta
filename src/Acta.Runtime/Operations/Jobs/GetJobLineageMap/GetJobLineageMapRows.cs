namespace Acta.Runtime;

/// <summary>The five result sets the lineage read returns for one focused Job; the job nodes bind the public models.</summary>
internal sealed record GetJobLineageMapRows(
    GetJobLineageMapResponse.Node Focus,
    IReadOnlyList<GetJobLineageMapResponse.Node> Ancestors,
    IReadOnlyList<GetJobLineageMapStepRow> Steps,
    IReadOnlyList<GetJobLineageMapCheckpointRow> Checkpoints,
    IReadOnlyList<GetJobLineageMapResponse.Child> Children
);
