namespace Acta;

/// <summary>
/// Wire-shaped enqueue request for cross-process callers and <c>IJobs.EnqueueAsync(JobEnqueueRequest)</c>. Two mutually
/// exclusive channels feed the earliest claim instant: <paramref name="NextRunAtUtc"/>, an absolute UTC instant, and
/// <see cref="DelaySeconds"/>, resolved on the database clock at insert so a frontend's clock never affects scheduling;
/// both null means claimable at once. A non-null <paramref name="ParentJobId"/> enqueues a child of a non-terminal parent,
/// inheriting its lineage root and, when unset, its correlation id and tenant, with sibling-unique dedup. A
/// <paramref name="TenantKey"/> must name an active tenant; a cross-tenant child key needs <paramref name="OverrideParentTenant"/>.
/// </summary>
public sealed record JobEnqueueRequest(
    string JobNamespace,
    string JobName,
    JobPayload Input = default,
    string? DeduplicationKey = null,
    string? CorrelationKey = null,
    string? ConcurrencyKey = null,
    JobPriorityCode? Priority = null,
    DateTime? NextRunAtUtc = null,
    int? DelaySeconds = null,
    IReadOnlyList<TagInput>? Tags = null,
    long? ParentJobId = null,
    string? TenantKey = null,
    bool OverrideParentTenant = false,
    string? Lane = null
);
