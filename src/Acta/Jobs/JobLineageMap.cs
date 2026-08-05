using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// A compact runtime truth map for one focused Job: the ancestor context up to the lineage root, the
/// focused Job itself with its steps and the durable wait it is blocked on, and its direct children.
/// Like <see cref="JobExplanation"/> it is assembled from the same durable rows an operator could read
/// with a <c>SELECT</c> - Acta maps the work it runs rather than hiding it behind synthesized state.
/// Returned by <see cref="IJobs.GetLineageMapAsync"/>; the dashboard renders it as a lineage panel.
/// <para>V1 is shallow: <c>Ancestors</c> is context only (root first, immediate parent last, no steps
/// of their own), <c>Job</c> is the focused subtree root, and <c>Children</c> are the direct children
/// capped at <see cref="JobLineageMapOptions.ChildLimit"/> with <c>ChildrenHasMore</c> flagging a
/// truncated set. Recursive descent below direct children is deferred.</para>
/// </summary>
public sealed record JobLineageMap(
    IReadOnlyList<JobLineageJob> Ancestors,
    JobLineageJob Job,
    IReadOnlyList<JobLineageStep> Steps,
    JobLineageWait? ActiveWait,
    IReadOnlyList<JobLineageChild> Children,
    bool ChildrenHasMore
);

/// <summary>Read options for <see cref="IJobs.GetLineageMapAsync"/>.</summary>
/// <param name="ChildLimit">Max direct children returned; the read fetches one extra to set
/// <see cref="JobLineageMap.ChildrenHasMore"/>. Clamped to 1..1000; defaults to 100.</param>
public sealed record JobLineageMapOptions(int ChildLimit = 100);

/// <summary>
/// A Job node in the lineage map - the focused Job or one of its ancestors. Carries the public
/// <see cref="JobRef"/> forms alongside the JSON-hidden numeric ids, matching <see cref="JobListItem"/>.
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

/// <summary>One step slot of the focused Job, with <see cref="Explanation"/> as the state's plain-English meaning.</summary>
public sealed record JobLineageStep(string Name, JobStepStatusCode Status, string Explanation);

/// <summary>The durable wait the focused Job is blocked on: a signal name, or a timer slot and its due instant.</summary>
public sealed record JobLineageWait(JobLineageWaitKind Kind, string Name, DateTime? DueAtUtc);

/// <summary>A direct child of the focused Job, linkable to its own detail view.</summary>
public sealed record JobLineageChild(
    [property: JsonIgnore] long JobId,
    JobRef JobRef,
    string JobName,
    JobStatusCode Status,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc
);

/// <summary>Which durable wait primitive the focused Job is blocked on. Values match <see cref="JobCheckpointKindCode"/>.</summary>
public enum JobLineageWaitKind : byte
{
    /// <summary>Awaiting an external signal via <c>ctx.WaitSignalAsync</c>.</summary>
    Signal = 20,

    /// <summary>Awaiting a durable sleep timer's due instant.</summary>
    Timer = 30,
}
