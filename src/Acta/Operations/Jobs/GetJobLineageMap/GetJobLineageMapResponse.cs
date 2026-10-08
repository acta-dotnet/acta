namespace Acta;

/// <summary>
/// A compact runtime truth map for one focused Job: the ancestor context up to the lineage root, the focused Job with
/// its steps and the durable wait it is blocked on, and its direct children. Returned by
/// <see cref="IJobs.GetLineageMapAsync"/>; the dashboard renders it as a lineage panel. V1 is shallow: <c>Ancestors</c>
/// is context only (root first, immediate parent last, no steps of their own), <c>Job</c> is the focused subtree root,
/// and <c>Children</c> are the direct children capped at <see cref="GetJobLineageMapOptions.ChildLimit"/> with
/// <c>ChildrenHasMore</c> flagging a truncated set.
/// </summary>
public sealed record GetJobLineageMapResponse(
    IReadOnlyList<GetJobLineageMapNode> Ancestors,
    GetJobLineageMapNode Job,
    IReadOnlyList<GetJobLineageMapStep> Steps,
    GetJobLineageMapWait? ActiveWait,
    IReadOnlyList<GetJobLineageMapChild> Children,
    bool ChildrenHasMore
);
