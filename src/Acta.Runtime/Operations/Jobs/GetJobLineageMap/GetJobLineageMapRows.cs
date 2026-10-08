namespace Acta.Runtime;

/// <summary>The five result sets the lineage read returns for one focused Job; the job nodes bind the public models.</summary>
internal sealed record GetJobLineageMapRows(
    JobLineageJob Focus,
    IReadOnlyList<JobLineageJob> Ancestors,
    IReadOnlyList<GetJobLineageMapStepRow> Steps,
    IReadOnlyList<GetJobLineageMapCheckpointRow> Checkpoints,
    IReadOnlyList<JobLineageChild> Children
);
