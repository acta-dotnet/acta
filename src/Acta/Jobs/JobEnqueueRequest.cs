namespace Acta;

/// <summary>
/// Wire-shaped enqueue request. Used by cross-process callers and by
/// <c>IJobs.EnqueueAsync(JobEnqueueRequest)</c> when the caller already holds a serialized request.
/// </summary>
/// <remarks>
/// Two mutually exclusive delayed-enqueue channels feed the earliest claim instant:
/// <paramref name="NextRunAtUtc"/> is an absolute UTC instant (fixed wall-clock time), and
/// <see cref="DelaySeconds"/> a relative delay resolved on the database clock at insert
/// (<c>db_now + delay</c>), so an enqueue-only frontend's clock never affects scheduling. Both
/// <c>null</c> means claimable immediately; an instant ahead of now holds the Job at <c>Ready</c>.
/// A non-null <paramref name="ParentJobId"/> enqueues the row as a child of that Job: the parent must
/// exist and be non-terminal, the child inherits the parent's lineage root and (when unset) its
/// correlation id and tenant, and <paramref name="DeduplicationKey"/> dedup becomes sibling-unique.
/// A non-null <paramref name="TenantKey"/> scopes the Job to that registered tenant; an unknown or
/// inactive key rejects, as does a cross-tenant child key without <paramref name="OverrideParentTenant"/>.
/// </remarks>
public sealed record JobEnqueueRequest(
    string JobNamespace,
    string JobName,
    JobPayload Input = default,
    string? DeduplicationKey = null,
    string? CorrelationKey = null,
    string? ExclusiveKey = null,
    JobPriorityCode? Priority = null,
    DateTime? NextRunAtUtc = null,
    int? DelaySeconds = null,
    IReadOnlyList<TagInput>? Tags = null,
    long? ParentJobId = null,
    string? TenantKey = null,
    bool OverrideParentTenant = false
);
