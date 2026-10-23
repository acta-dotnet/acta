using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// A Job node in the lineage map - the focused Job or one of its ancestors. Carries the public
/// <see cref="JobRef"/> forms alongside the JSON-hidden numeric ids, matching <see cref="ListJobsItem"/>.
/// </summary>
public sealed record GetJobLineageMapNode(
    [property: JsonIgnore] long JobId,
    JobRef JobRef,
    string JobNamespace,
    string JobName,
    JobStatusCode Status,
    [property: JsonIgnore] long? ParentJobId,
    JobRef? ParentJobRef,
    [property: JsonIgnore] long? LineageRootJobId,
    JobRef? LineageRootJobRef,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc
);
