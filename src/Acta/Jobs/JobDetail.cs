using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// Read projection of a Job row exposed by <c>IJobs.GetAsync</c>: identity, linkage, status,
/// scheduling, leasing, retention, and input format. The input payload itself is never returned.
/// Property order mirrors the <c>jobs</c> entity (identity, scope/routing, caller keys, input) and
/// then its 1:1 <c>runtimes</c> row, so the shape reads in the same order as the schema; a column
/// resolved to a public value follows the id it came from, and the created/modified pair closes the
/// record together. JSON serialization carries the public refs and hides the numeric ids.
/// </summary>
public sealed record JobDetail(
    // Identity.
    [property: JsonIgnore] long JobId,
    JobRef JobRef,
    // Scope / routing.
    string JobNamespace,
    // Surrogate for the namespace+name pair; non-null because the job row's definition_id is NOT NULL.
    int JobDefinitionId,
    string JobName,
    [property: JsonIgnore] long? LineageRootId,
    JobRef? LineageRootJobRef,
    [property: JsonIgnore] long? ParentJobId,
    JobRef? ParentJobRef,
    int? TenantId,
    string? TenantKey,
    // Caller keys.
    string? DeduplicationKey,
    string? CorrelationKey,
    string? ExclusiveKey,
    // Input.
    byte InputFormatId,
    // Runtime row.
    JobStatusCode Status,
    JobPriorityCode Priority,
    DateTime? NextRunAtUtc,
    int ExecutionNumber,
    short FailureCount,
    int? LeasedByWorkerId,
    DateTime? LeaseExpiresAtUtc,
    DateTime? RetentionUntilUtc,
    // Audit.
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc
);
