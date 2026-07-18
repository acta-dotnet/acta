namespace Acta;

/// <summary>
/// Common per-enqueue choices for the typed
/// <see cref="IJobs.EnqueueAsync{TInput}(TInput, JobEnqueueOptions, CancellationToken)"/> facade, the
/// alternative to constructing a wire <see cref="JobEnqueueRequest"/>. Every member is optional; the
/// job name, namespace, and payload format are resolved from the input type's generated descriptor.
/// </summary>
public class JobEnqueueOptions
{
    /// <summary>
    /// Namespace to resolve the input type within. Supply this only to disambiguate an input type
    /// registered under more than one namespace; when <c>null</c> the type is resolved globally and
    /// resolution throws if it is ambiguous.
    /// </summary>
    public string? Namespace { get; init; }

    /// <summary>
    /// Exact final deduplication key, unique per namespace for root jobs. A second enqueue with the
    /// same key returns the existing Job (<see cref="JobEnqueueAction.Deduplicated"/>).
    /// </summary>
    public string? DeduplicationKey { get; init; }

    /// <summary>
    /// Correlation id for cross-system tracing: a W3C trace id or a caller's own custom value, at most
    /// 64 chars. <c>null</c> = uncorrelated.
    /// </summary>
    public string? CorrelationKey { get; init; }

    /// <summary>
    /// Named mutual-exclusion key; at most one Job per <c>(namespace, key)</c> executes at a time
    /// (mutual exclusion only, no per-key ordering).
    /// </summary>
    public string? ExclusiveKey { get; init; }

    /// <summary>
    /// Claim-order priority override. <c>null</c> = the definition's declared priority.
    /// </summary>
    public JobPriorityCode? Priority { get; init; }

    /// <summary>
    /// Tags attached at enqueue (name + optional value); names must be unique within the request.
    /// </summary>
    public IReadOnlyList<TagInput>? Tags { get; init; }

    /// <summary>
    /// Absolute caller-supplied claim instant (delayed enqueue). <c>null</c> = no absolute time.
    /// Treated as UTC. Mutually exclusive with <see cref="DelaySeconds"/>; for a relative delay prefer
    /// <see cref="DelaySeconds"/> so the frontend clock does not affect scheduling.
    /// </summary>
    public DateTime? NextRunAtUtc { get; init; }

    /// <summary>
    /// Relative delayed-enqueue resolved on the database clock (<c>db_now + DelaySeconds</c>) at
    /// insert. <c>null</c> = no relative delay. Mutually exclusive with <see cref="NextRunAtUtc"/>.
    /// </summary>
    public int? DelaySeconds { get; init; }

    /// <summary>
    /// Enqueue as a child of this Job. The parent must exist and be non-terminal; the row inherits
    /// the parent's lineage root and, when <see cref="CorrelationKey"/> is unset, its correlation id.
    /// <see cref="DeduplicationKey"/> dedup is scoped to the direct parent (sibling-unique) instead of the
    /// namespace. <c>null</c> = root job.
    /// </summary>
    public long? ParentId { get; init; }

    /// <summary>
    /// Registered tenant this Job is <em>about</em> (the customer / business entity), resolved to a tenant
    /// id at insert. An opaque external key (GUID / ULID / customer code); an unknown or inactive key
    /// rejects the enqueue. <c>null</c> = no tenant for a root Job; a child Job with <c>null</c> here
    /// inherits its parent's tenant.
    /// </summary>
    public string? TenantKey { get; init; }
}
