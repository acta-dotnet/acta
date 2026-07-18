using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// Read projection of a Job row exposed by <c>IJobs.GetAsync</c>: identity, linkage, status,
/// scheduling, leasing, retention, and input format. The input payload itself is never returned.
/// Property order mirrors the <c>job</c> row; JSON serialization carries the public refs and hides
/// the numeric ids.
/// </summary>
public sealed record JobSnapshot(
    [property: JsonIgnore] long JobId,
    JobRef JobRef,
    [property: JsonIgnore] long? LineageRootId,
    JobRef? LineageRootJobRef,
    [property: JsonIgnore] long? ParentJobId,
    JobRef? ParentJobRef,
    string? DeduplicationKey,
    string? CorrelationKey,
    string JobNamespace,
    string JobName,
    int? TenantId,
    JobStatusCode Status,
    JobPriorityCode Priority,
    int ExecutionNumber,
    short FailureCount,
    byte InputFormatId,
    DateTime? NextRunAtUtc,
    int? LeasedByWorkerId,
    DateTime? LeaseExpiresAtUtc,
    string? ExclusiveKey,
    DateTime? RetentionUntilUtc,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc
);
