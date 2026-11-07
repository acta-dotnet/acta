namespace Acta;

/// <summary>
/// A map around one job: its nearest ancestors, the job itself with its newest steps and the wait it is suspended on, and its direct
/// children. Each list that was cut says so in its hasMore member.
/// </summary>
/// <param name="Ancestors">The job's nearest ancestors, at most 50, the farthest first and its parent last.</param>
/// <param name="AncestorsHasMore">Whether the lineage has more ancestors than the ones listed.</param>
/// <param name="Job">The focused job.</param>
/// <param name="Steps">The focused job's newest steps, at most 50, oldest first; stepsHasMore says when older ones were left out.</param>
/// <param name="StepsHasMore">Whether the job has older steps than the ones listed.</param>
/// <param name="ActiveWait">The wait the job is suspended on; null when it is not waiting.</param>
/// <param name="Children">The job's direct children, at most the limit asked for, which is 100 on the job detail read.</param>
/// <param name="ChildrenHasMore">Whether the job has more children than the ones listed.</param>
public sealed record GetJobLineageMapResponse(
    IReadOnlyList<GetJobLineageMapNode> Ancestors,
    bool AncestorsHasMore,
    GetJobLineageMapNode Job,
    IReadOnlyList<GetJobLineageMapStep> Steps,
    bool StepsHasMore,
    GetJobLineageMapWait? ActiveWait,
    IReadOnlyList<GetJobLineageMapChild> Children,
    bool ChildrenHasMore
);
