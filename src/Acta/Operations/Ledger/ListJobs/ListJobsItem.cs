using System.Text.Json.Serialization;

namespace Acta;

/// <summary>One job in a page: its identity, lineage, status, and timing. The input is not included.</summary>
/// <param name="JobId">The job's internal id; the JSON omits it and carries jobRef.</param>
/// <param name="TenantId">The internal id of the job's tenant; the JSON omits it and carries tenantName.</param>
/// <param name="ParentJobId">The parent job's internal id; the JSON omits it and carries parentJobRef.</param>
/// <param name="LineageRootJobId">The lineage root job's internal id; the JSON omits it and carries lineageRootJobRef.</param>
/// <param name="JobRef">The job's ref, job_ followed by 32 hex digits.</param>
/// <param name="JobNamespace">The namespace of the job's definition.</param>
/// <param name="JobName">The name of the job's definition.</param>
/// <param name="TenantName">The tenant the job is about, when it names one.</param>
/// <param name="ParentJobRef">The ref of the job that started this one as a child; null for a job without a parent.</param>
/// <param name="LineageRootJobRef">The ref of the job at the root of the lineage this job belongs to; null for a root job.</param>
/// <param name="DeduplicationKey">The job's deduplication key, when it was enqueued with one.</param>
/// <param name="CorrelationKey">The caller's correlation key, when the job was enqueued with one.</param>
/// <param name="Status">The job's status.</param>
/// <param name="Priority">The job's priority.</param>
/// <param name="Lane">The lane the job runs in, or null; a lane runs one job at a time, in enqueue order.</param>
/// <param name="CreatedAtUtc">When the row was created, in UTC.</param>
/// <param name="ModifiedAtUtc">When the row last changed, in UTC.</param>
/// <param name="NextRunAtUtc">When the job next becomes claimable, in UTC; null when it is not waiting on a time.</param>
/// <param name="ExecutionNumber">How many times the job has been claimed; retries and reclaims count.</param>
/// <param name="FailureCount">How many attempts in a row have failed; a success resets it.</param>
public sealed record ListJobsItem(
    [property: JsonIgnore] long JobId,
    JobRef JobRef,
    string JobNamespace,
    string JobName,
    [property: JsonIgnore] int? TenantId,
    string? TenantName,
    [property: JsonIgnore] long? ParentJobId,
    JobRef? ParentJobRef,
    [property: JsonIgnore] long? LineageRootJobId,
    JobRef? LineageRootJobRef,
    string? DeduplicationKey,
    string? CorrelationKey,
    JobStatusCode Status,
    JobPriorityCode Priority,
    string? Lane,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc,
    DateTime? NextRunAtUtc,
    int ExecutionNumber,
    int FailureCount
);
