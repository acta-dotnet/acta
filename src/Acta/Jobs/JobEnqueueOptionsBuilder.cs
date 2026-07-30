namespace Acta;

/// <summary>
/// Fluent builder for <see cref="JobEnqueueOptions"/>, used by the typed
/// <c>IJobs.EnqueueAsync(input, configure)</c> overload. Validation is eager at each setter; tag
/// dedupe is last-write-wins; <see cref="Build"/> snapshots repeatably.
/// </summary>
public sealed class JobEnqueueOptionsBuilder
{
    private string? _namespace;
    private string? _deduplicationKey;
    private string? _correlationKey;
    private string? _exclusiveKey;
    private JobPriorityCode? _priority;
    private DateTime? _nextRunAtUtc;
    private int? _delaySeconds;
    private long? _parentId;
    private string? _tenantKey;
    private bool _overrideParentTenant;
    private readonly Dictionary<string, TagInput> _tags = new(StringComparer.Ordinal);

    /// <summary>
    /// Namespace to resolve the input type within; only needed when the type is registered under
    /// more than one namespace.
    /// </summary>
    public JobEnqueueOptionsBuilder Namespace(string namespaceName)
    {
        namespaceName = IdentifierSyntax.CanonicalizeUserKebab(namespaceName, nameof(namespaceName));
        _namespace = namespaceName;
        return this;
    }

    /// <summary>
    /// Set an already resolved final deduplication key. Typed routing has not resolved the definition
    /// when options are configured, so callers compose definition-scoped keys with
    /// <see cref="Acta.DeduplicationKey"/> before assigning them here.
    /// </summary>
    public JobEnqueueOptionsBuilder DeduplicationKey(string deduplicationKey)
    {
        deduplicationKey = IdentifierSyntax.NormalizeKey(deduplicationKey, nameof(deduplicationKey));
        _deduplicationKey = deduplicationKey;
        return this;
    }

    /// <summary>
    /// Correlation id for cross-system tracing: a W3C trace id or a caller's own custom value, at most
    /// 64 chars.
    /// </summary>
    public JobEnqueueOptionsBuilder CorrelationKey(string correlationKey)
    {
        IdentifierSyntax.ValidateExternalToken(correlationKey, nameof(correlationKey), IdentifierSyntax.DefaultMaxLength);
        _correlationKey = correlationKey;
        return this;
    }

    /// <summary>
    /// Named mutual-exclusion key; at most one Job per (namespace, key) is in-flight at a time.
    /// </summary>
    public JobEnqueueOptionsBuilder ExclusiveKey(string exclusiveKey)
    {
        exclusiveKey = IdentifierSyntax.NormalizeKey(exclusiveKey, nameof(exclusiveKey));
        _exclusiveKey = exclusiveKey;
        return this;
    }

    /// <summary>
    /// Claim-order priority override for this row only.
    /// </summary>
    public JobEnqueueOptionsBuilder Priority(JobPriorityCode priority)
    {
        _priority = priority;
        return this;
    }

    /// <summary>
    /// Hold the earliest claim until the absolute instant <paramref name="utc"/> (delayed enqueue),
    /// the explicit fixed-wall-clock-time path. Normalized to UTC. Last-call-wins with
    /// <see cref="Delayed"/>, which it clears.
    /// </summary>
    public JobEnqueueOptionsBuilder NextExecutionAt(DateTimeOffset utc)
    {
        _nextRunAtUtc = utc.UtcDateTime;
        _delaySeconds = null;
        return this;
    }

    /// <summary>
    /// Delay the earliest claim by <paramref name="delay"/>, resolved against the database clock
    /// (<c>db_now + delay</c>) at enqueue so the caller's clock never affects scheduling. Must be
    /// non-negative; rounded up to whole seconds. Last-call-wins with <see cref="NextExecutionAt"/>,
    /// which it clears.
    /// </summary>
    public JobEnqueueOptionsBuilder Delayed(TimeSpan delay)
    {
        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay), delay, "Delay must be non-negative.");
        }
        _delaySeconds = checked((int)Math.Ceiling(delay.TotalSeconds));
        _nextRunAtUtc = null;
        return this;
    }

    /// <summary>
    /// Enqueue as a child of the given Job. The parent must exist and be non-terminal; deduplication-key
    /// dedup becomes sibling-unique per parent.
    /// </summary>
    public JobEnqueueOptionsBuilder ParentId(long parentJobId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parentJobId);
        _parentId = parentJobId;
        return this;
    }

    /// <summary>
    /// Scope this Job to a registered tenant (the customer / business entity it is about). The opaque
    /// normalized tenant key (GUID / ULID / customer code folded to Acta's key shape) is resolved to a
    /// tenant id at insert; an unknown or inactive tenant rejects the enqueue. On a child enqueue a
    /// key that differs from the parent's tenant is rejected unless <paramref name="overrideParent"/>
    /// explicitly opts into the cross-tenant lineage.
    /// </summary>
    public JobEnqueueOptionsBuilder TenantKey(string tenantKey, bool overrideParent = false)
    {
        tenantKey = IdentifierSyntax.NormalizeTenantKey(tenantKey, nameof(tenantKey));
        _tenantKey = tenantKey;
        _overrideParentTenant = overrideParent;
        return this;
    }

    /// <summary>
    /// Add or replace a single tag (dotted-kebab name, optional value). Same-name calls follow
    /// last-write-wins.
    /// </summary>
    public JobEnqueueOptionsBuilder Tag(string name, string? value = null)
    {
        name = IdentifierSyntax.CanonicalizeUserDottedKebab(name, nameof(name), IdentifierSyntax.ExtendedMaxLength);
        if (value is { } tagValue)
        {
            IdentifierSyntax.ValidateDisplayValue(tagValue, nameof(value), IdentifierSyntax.ExtendedMaxLength);
        }
        _tags[name] = new TagInput(name, value);
        return this;
    }

    /// <summary>
    /// Snapshot the current builder state into a fresh <see cref="JobEnqueueOptions"/>. Safe to call
    /// repeatedly; subsequent mutation does not affect prior results.
    /// </summary>
    public JobEnqueueOptions Build() =>
        new()
        {
            Namespace = _namespace,
            DeduplicationKey = _deduplicationKey,
            CorrelationKey = _correlationKey,
            ExclusiveKey = _exclusiveKey,
            Priority = _priority,
            Tags = _tags.Count == 0 ? null : [.. _tags.Values],
            NextRunAtUtc = _nextRunAtUtc,
            DelaySeconds = _delaySeconds,
            ParentId = _parentId,
            TenantKey = _tenantKey,
            OverrideParentTenant = _overrideParentTenant,
        };
}
