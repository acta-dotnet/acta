using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// A compact runtime truth map for one focused Job: the ancestor context up to the lineage root, the focused Job with
/// its steps and the durable wait it is blocked on, and its direct children, assembled like <see cref="JobExplanation"/>
/// from the same rows a <c>SELECT</c> would read. Returned by <see cref="IJobs.GetLineageMapAsync"/>; the dashboard
/// renders it as a lineage panel. V1 is shallow: <c>Ancestors</c> is context only (root first, immediate parent last,
/// no steps of their own), <c>Job</c> is the focused subtree root, and <c>Children</c> are the direct children capped
/// at <see cref="JobLineageMapOptions.ChildLimit"/> with <c>ChildrenHasMore</c> flagging a truncated set.
/// </summary>
public sealed record JobLineageMap(
    IReadOnlyList<JobLineageJob> Ancestors,
    JobLineageJob Job,
    IReadOnlyList<JobExplainStep> Steps,
    JobExplainWait? ActiveWait,
    IReadOnlyList<JobLineageChild> Children,
    bool ChildrenHasMore
);

/// <summary>Read options for <see cref="IJobs.GetLineageMapAsync"/>.</summary>
/// <param name="ChildLimit">Max direct children returned; the read fetches one extra to set
/// <see cref="JobLineageMap.ChildrenHasMore"/>. Clamped to 1..1000; defaults to 100.</param>
public sealed record JobLineageMapOptions(int ChildLimit = 100);

/// <summary>
/// A Job node in the lineage map - the focused Job or one of its ancestors. Carries the public
/// <see cref="JobRef"/> forms alongside the JSON-hidden numeric ids, matching <see cref="ListJobsItem"/>.
/// </summary>
public sealed record JobLineageJob(
    [property: JsonIgnore] long JobId,
    JobRef JobRef,
    string JobNamespace,
    string JobName,
    JobStatusCode Status,
    [property: JsonIgnore] long? ParentJobId,
    JobRef? ParentJobRef,
    [property: JsonIgnore] long? LineageRootId,
    JobRef? LineageRootJobRef,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc
);

/// <summary>A direct child of the focused Job, linkable to its own detail view.</summary>
public sealed record JobLineageChild(
    [property: JsonIgnore] long JobId,
    JobRef JobRef,
    string JobName,
    JobStatusCode Status,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc
);
