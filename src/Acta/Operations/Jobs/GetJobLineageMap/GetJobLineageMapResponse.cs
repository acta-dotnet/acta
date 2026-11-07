namespace Acta;

/// <summary>
/// A compact runtime truth map for one focused Job, returned by <see cref="IJobs.GetLineageMapAsync"/> for the
/// dashboard's lineage panel. <c>Ancestors</c> are context only (farthest first, immediate parent last, no steps of
/// their own), the nearest <see cref="GetJobLineageMapHandler.AncestorLimit"/>; <c>Job</c> is the focused Job with its newest
/// <see cref="GetJobLineageMapHandler.StepLimit"/> steps and the durable wait it is blocked on; <c>Children</c> are its
/// direct children up to <see cref="GetJobLineageMapOptions.ChildLimit"/>. Each <c>HasMore</c> flags a truncated set.
/// </summary>
/// <param name="Ancestors">The job's nearest ancestors, the farthest first and its parent last.</param>
/// <param name="AncestorsHasMore">Whether the lineage has more ancestors than the ones listed.</param>
/// <param name="Job">The focused job.</param>
/// <param name="Steps">The focused job's newest steps, oldest first; stepsHasMore says when older ones were left out.</param>
/// <param name="StepsHasMore">Whether the job has older steps than the ones listed.</param>
/// <param name="ActiveWait">The wait the job is suspended on; null when it is not waiting.</param>
/// <param name="Children">The job's direct children, up to the requested limit.</param>
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
