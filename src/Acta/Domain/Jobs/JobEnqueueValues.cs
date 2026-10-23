namespace Acta;

/// <summary>
/// Caller-supplied per-row enqueue request, shared by the scalar <c>EnqueueJob</c> and the batched <c>EnqueueJobs</c>
/// operations. <paramref name="ConcurrencyKey"/> is a named mutual-exclusion key (null is unconstrained): at most one
/// Job per <c>(JobNamespace, ConcurrencyKey)</c> executes at a time, enforced at execution admission, where a loser
/// re-arms Ready after a fixed bounce delay (mutual exclusion only, no per-key ordering). Two mutually exclusive
/// channels feed the earliest claim instant: <paramref name="NextRunAtUtc"/>, a caller-supplied absolute instant, and
/// <paramref name="DelaySeconds"/>, resolved by the routine as <c>db_now + delay</c>; both null stamps the enqueue time.
/// </summary>
internal sealed record JobEnqueueValues(
    string NamespaceName,
    string JobName,
    JobPayload Input,
    JobPriorityCode? PriorityOverride = null,
    string? DeduplicationKey = null,
    string? CorrelationKey = null,
    string? ConcurrencyKey = null,
    DateTime? NextRunAtUtc = null,
    int? DelaySeconds = null,
    IReadOnlyList<TagInput>? Tags = null,
    long? ParentJobId = null,
    string? TenantName = null,
    bool OverrideParentTenant = false,
    string? Lane = null
);
