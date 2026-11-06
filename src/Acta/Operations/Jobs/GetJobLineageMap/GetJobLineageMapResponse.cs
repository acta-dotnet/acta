namespace Acta;

/// <summary>
/// A compact runtime truth map for one focused Job, returned by <see cref="IJobs.GetLineageMapAsync"/> for the
/// dashboard's lineage panel. <c>Ancestors</c> are context only (farthest first, immediate parent last, no steps of
/// their own), the nearest <see cref="GetJobLineageMapHandler.AncestorLimit"/>; <c>Job</c> is the focused Job with its newest
/// <see cref="GetJobLineageMapHandler.StepLimit"/> steps and the durable wait it is blocked on; <c>Children</c> are its
/// direct children up to <see cref="GetJobLineageMapOptions.ChildLimit"/>. Each <c>HasMore</c> flags a truncated set.
/// </summary>
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
