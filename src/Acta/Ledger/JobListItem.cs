using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// One job row in a <see cref="ILedger.ListJobsAsync"/> page. Carries identity, lifecycle, and
/// audit-facing fields only; the input payload is never exposed by list reads. JSON serialization
/// carries the public refs and hides the numeric ids.
/// </summary>
/// <param name="JobId">Internal sequence-allocated job id.</param> <param name="JobRef">Public stable job reference.</param>
/// <param name="JobNamespace">Owning namespace name.</param> <param name="JobName">Job definition name.</param> <param name="TenantId">Resolved tenant id, or null.</param> <param name="TenantKey">Tenant's caller-supplied key resolved from the catalog, or null.</param>
/// <param name="ParentJobId">Parent job id for child jobs, or null for roots.</param> <param name="ParentJobRef">Parent job's public ref, or null for roots.</param>
/// <param name="LineageRootId">Root id of the job's lineage tree, or null.</param> <param name="LineageRootJobRef">Lineage root's public ref, or null.</param>
/// <param name="DeduplicationKey">Caller-supplied deduplication key, or null.</param> <param name="CorrelationKey">Caller-supplied correlation id (trace / request / order id), or null.</param> <param name="Status">Current lifecycle status.</param> <param name="Priority">Claim priority.</param>
/// <param name="CreatedAtUtc">Row insert instant.</param> <param name="ModifiedAtUtc">Last row change instant.</param> <param name="NextRunAtUtc">Next due instant, or null.</param>
/// <param name="ExecutionNumber">Attempt counter.</param> <param name="FailureCount">Consecutive-failure count behind the retry budget.</param>
public sealed record JobListItem(
    [property: JsonIgnore] long JobId,
    JobRef JobRef,
    string JobNamespace,
    string JobName,
    int? TenantId,
    string? TenantKey,
    [property: JsonIgnore] long? ParentJobId,
    JobRef? ParentJobRef,
    [property: JsonIgnore] long? LineageRootId,
    JobRef? LineageRootJobRef,
    string? DeduplicationKey,
    string? CorrelationKey,
    JobStatusCode Status,
    JobPriorityCode Priority,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc,
    DateTime? NextRunAtUtc,
    int ExecutionNumber,
    short FailureCount
);
