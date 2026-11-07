using System.Text.Json.Serialization;

namespace Acta;

/// <summary>A job in the lineage map: the job the map is about, or one of its ancestors.</summary>
/// <param name="JobId">The job's internal id; the JSON omits it and carries jobRef.</param>
/// <param name="ParentJobId">The parent job's internal id; the JSON omits it and carries parentJobRef.</param>
/// <param name="LineageRootJobId">The lineage root job's internal id; the JSON omits it and carries lineageRootJobRef.</param>
/// <param name="JobRef">The job's ref, job_ followed by 32 hex digits.</param>
/// <param name="JobNamespace">The namespace of the job's definition.</param>
/// <param name="JobName">The name of the job's definition.</param>
/// <param name="Status">The job's status.</param>
/// <param name="ParentJobRef">The ref of the job that started this one as a child; null for a job without a parent.</param>
/// <param name="LineageRootJobRef">The ref of the job at the root of the lineage this job belongs to; null for a root job.</param>
/// <param name="CreatedAtUtc">When the row was created, in UTC.</param>
/// <param name="ModifiedAtUtc">When the row last changed, in UTC.</param>
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
