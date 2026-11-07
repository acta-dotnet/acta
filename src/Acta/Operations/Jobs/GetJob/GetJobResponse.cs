using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// Read projection of a Job row exposed by <c>IJobs.GetAsync</c>: identity, linkage, status, scheduling, leasing,
/// retention, and input format; the input payload itself is never returned. Property order mirrors the <c>jobs</c>
/// entity (identity, scope and routing, caller keys, input) and then its 1:1 <c>jobs_state</c> row, so the shape reads
/// in schema order; a column resolved to a public value follows the id it came from, and the created/modified pair
/// closes the record. JSON carries the public refs and hides the numeric ids. <c>DefinitionId</c> is the surrogate
/// for the namespace+name pair, non-null because the row's definition_id is NOT NULL.
/// </summary>
/// <param name="JobId">The job's internal id; the JSON omits it and carries jobRef.</param>
/// <param name="DefinitionId">The internal id of the job's definition; the JSON omits it.</param>
/// <param name="LineageRootJobId">The lineage root job's internal id; the JSON omits it and carries lineageRootJobRef.</param>
/// <param name="ParentJobId">The parent job's internal id; the JSON omits it and carries parentJobRef.</param>
/// <param name="TenantId">The internal id of the job's tenant; the JSON omits it and carries tenantName.</param>
/// <param name="BlockedBehindJobId">The internal id of the job this one is blocked behind; the JSON omits it and carries
/// blockedBehindJobRef.</param>
/// <param name="LeasedByWorkerId">The internal id of the worker holding the lease; the JSON omits it and carries
/// leasedByWorkerRef.</param>
/// <param name="JobRef">The job's ref, job_ followed by 32 hex digits.</param>
/// <param name="Kind">Whether the job is an ordinary job or a scheduled definition's permanent slot.</param>
/// <param name="JobNamespace">The namespace of the job's definition.</param>
/// <param name="JobName">The name of the job's definition.</param>
/// <param name="LineageRootJobRef">The ref of the job at the root of the lineage this job belongs to.</param>
/// <param name="ParentJobRef">The ref of the job that started this one as a child; null for a job without a parent.</param>
/// <param name="TenantName">The tenant the job is about, when it names one.</param>
/// <param name="DeduplicationKey">The job's deduplication key, when it was enqueued with one.</param>
/// <param name="CorrelationKey">The caller's correlation key, when the job was enqueued with one.</param>
/// <param name="ConcurrencyKey">The job's concurrency key, when it was enqueued with one; jobs that share a key run at most as many at
/// once as the definition's concurrency limit allows.</param>
/// <param name="InputFormatId">The id of the format the job's input is stored in; 0 when the job has no input.</param>
/// <param name="Status">The job's status.</param>
/// <param name="Priority">The job's priority.</param>
/// <param name="Lane">The lane the job runs in, or null; a lane runs one job at a time, in enqueue order.</param>
/// <param name="BlockedBehindJobRef">While the job is blocked behind its lane, the ref of the job that moves the lane on: the lane's
/// running job, or else its oldest unfinished one. Null otherwise.</param>
/// <param name="NextRunAtUtc">When the job next becomes claimable, in UTC; null when it is not waiting on a time.</param>
/// <param name="ExecutionNumber">How many times the job has been claimed; retries and reclaims count.</param>
/// <param name="FailureCount">How many attempts in a row have failed; a success resets it.</param>
/// <param name="LeaseExpiresAtUtc">When the lease of the worker running the job expires, in UTC; null when no worker holds one.</param>
/// <param name="RetentionUntilUtc">When retention may delete the job, in UTC; null when no such time is set.</param>
/// <param name="CreatedAtUtc">When the row was created, in UTC.</param>
/// <param name="ModifiedAtUtc">When the row last changed, in UTC.</param>
/// <param name="LeasedByWorkerRef">The ref of the worker that holds the job's lease; null when none does.</param>
/// <param name="Version">The job's version, which every write to it increments; send it as expectedVersion on a control to make that call
/// a compare-and-set.</param>
public sealed record GetJobResponse(
    [property: JsonIgnore] long JobId,
    JobRef JobRef,
    JobKindCode Kind,
    string JobNamespace,
    [property: JsonIgnore] int DefinitionId,
    string JobName,
    [property: JsonIgnore] long? LineageRootJobId,
    JobRef? LineageRootJobRef,
    [property: JsonIgnore] long? ParentJobId,
    JobRef? ParentJobRef,
    [property: JsonIgnore] int? TenantId,
    string? TenantName,
    string? DeduplicationKey,
    string? CorrelationKey,
    string? ConcurrencyKey,
    byte InputFormatId,
    JobStatusCode Status,
    JobPriorityCode Priority,
    string? Lane,
    [property: JsonIgnore] long? BlockedBehindJobId,
    JobRef? BlockedBehindJobRef,
    DateTime? NextRunAtUtc,
    int ExecutionNumber,
    int FailureCount,
    [property: JsonIgnore] int? LeasedByWorkerId,
    DateTime? LeaseExpiresAtUtc,
    DateTime? RetentionUntilUtc,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc,
    WorkerRef? LeasedByWorkerRef,
    int Version
);
