using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// One job row in a <see cref="ILedger.ListJobsAsync"/> page: identity, lifecycle, and audit-facing fields only; the
/// input payload is never exposed by list reads. JSON carries the public refs and hides the numeric ids.
/// <c>CorrelationKey</c> is the caller-supplied trace, request, or order id, <c>ExecutionNumber</c> the attempt
/// counter, and <c>FailureCount</c> the consecutive-failure count behind the retry budget.
/// </summary>
public sealed record ListJobsItem(
    [property: JsonIgnore] long JobId,
    JobRef JobRef,
    string JobNamespace,
    string JobName,
    [property: JsonIgnore] int? TenantId,
    string? TenantName,
    [property: JsonIgnore] long? ParentJobId,
    JobRef? ParentJobRef,
    [property: JsonIgnore] long? LineageRootId,
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
