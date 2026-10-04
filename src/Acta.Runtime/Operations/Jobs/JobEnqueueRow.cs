namespace Acta.Runtime;

/// <summary>
/// Caller-supplied per-row enqueue request, shared by the scalar <c>EnqueueOne</c> and the
/// batched <c>EnqueueBatch</c> operations.
/// </summary>
/// <remarks>
/// <paramref name="ConcurrencyKey"/> is a named mutual-exclusion key (null is unconstrained): at most
/// one Job per <c>(JobNamespace, ConcurrencyKey)</c> executes at a time. The worker enforces this at
/// execution admission: after claim it takes the key's lock-store lock, and a loser re-arms Ready
/// after a fixed bounce delay (mutual exclusion only, no per-key ordering). Two mutually exclusive
/// delayed-enqueue channels feed the earliest claim instant: <paramref name="NextRunAtUtc"/> is a
/// caller-supplied absolute instant, and <paramref name="DelaySeconds"/> a relative delay the routine
/// resolves on the database clock as <c>db_now + delay</c>. When both are null the routine stamps the
/// enqueue time, so the row is claimable immediately.
/// </remarks>
internal sealed record JobEnqueueRow(
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
    long? ParentId = null,
    string? TenantKey = null,
    bool OverrideParentTenant = false,
    string? Lane = null
);

/// <summary>
/// The (ordinal, id, ref, action) outcome row both enqueue routines return; the scalar routine
/// always returns ordinal 0.
/// </summary>
[DbProjection]
internal readonly record struct EnqueueOutcomeRow(int Ordinal, long JobId, Guid JobRef, JobEnqueueAction Action);
