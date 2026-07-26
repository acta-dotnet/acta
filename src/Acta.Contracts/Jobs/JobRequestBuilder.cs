using System.Diagnostics.CodeAnalysis;

namespace Acta;

/// <summary>
/// Fluent builder for <see cref="JobEnqueueRequest"/> with named, validated, chainable setters. The
/// builder produces wire-shaped requests only: no runtime dependencies, no provider coupling, and no
/// direct reach into the durable substrate. Hand the result to <see cref="IJobs.EnqueueAsync(JobEnqueueRequest, CancellationToken)"/>.
/// </summary>
/// <remarks>
/// Validation is eager: identifiers, lengths, and system-prefix rules are checked at the supplying
/// call site, not at <see cref="Build"/> (<see cref="ArgumentNullException"/> for nulls,
/// <see cref="ArgumentException"/> for shape, length, or prefix violations). Tag dedup is
/// last-write-wins: repeated <see cref="Tag"/> calls or duplicate entries in <see cref="Tags"/> with
/// the same name silently replace the previous value, preserving insertion order. <see cref="Build"/>
/// is repeatable: each call snapshots the current field state into a fresh
/// <see cref="JobEnqueueRequest"/>, so subsequent mutation cannot leak into a prior result, and the
/// returned <see cref="JobEnqueueRequest.Tags"/> is <c>null</c> when no tags were added or a fresh
/// <see cref="TagInput"/> array otherwise, never the builder's internal container.
/// </remarks>
public sealed class JobRequestBuilder
{
    private readonly string _jobNamespace;
    private readonly string _jobName;
    private JobPayload _input;
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

    private JobRequestBuilder(string jobNamespace, string jobName)
    {
        _jobNamespace = jobNamespace;
        _jobName = jobName;
    }

    /// <summary>
    /// Start a new builder for a job under <paramref name="jobNamespace"/> / <paramref name="jobName"/>.
    /// Both identifiers are validated against the strict-kebab user-name rules immediately.
    /// </summary>
    public static JobRequestBuilder Create(string jobNamespace, string jobName)
    {
        jobNamespace = IdentifierSyntax.CanonicalizeUserKebab(jobNamespace, nameof(jobNamespace));
        jobName = IdentifierSyntax.CanonicalizeUserKebab(jobName, nameof(jobName), IdentifierSyntax.ExtendedMaxLength);
        return new JobRequestBuilder(jobNamespace, jobName);
    }

    /// <summary>
    /// Set the input payload to <paramref name="payload"/> verbatim. Accepts any
    /// <see cref="JobPayload"/> including <c>default</c> (= <see cref="JobPayload.None"/>).
    /// Last-call-wins with the other payload setters (<see cref="Json"/>, <see cref="Text"/>,
    /// <see cref="Bytes"/>, <see cref="NoPayload"/>).
    /// </summary>
    public JobRequestBuilder Payload(JobPayload payload)
    {
        _input = payload;
        return this;
    }

    /// <summary>
    /// JSON-encode <paramref name="value"/> through <see cref="JobPayload.Json{T}(T)"/> and set the input.
    /// </summary>
    [RequiresUnreferencedCode(
        "Reflection-based JSON serialization. Under trimming or Native AOT use Payload(JobPayload.Json(value, typeInfo)) with a source-generated JsonTypeInfo<T>."
    )]
    [RequiresDynamicCode(
        "Reflection-based JSON serialization. Under trimming or Native AOT use Payload(JobPayload.Json(value, typeInfo)) with a source-generated JsonTypeInfo<T>."
    )]
    public JobRequestBuilder Json<T>(T value) => Payload(JobPayload.Json(value));

    /// <summary>
    /// UTF-8-encode <paramref name="value"/> through <see cref="JobPayload.Text"/> and set the input.
    /// Empty string is allowed.
    /// </summary>
    public JobRequestBuilder Text(string value) => Payload(JobPayload.Text(value));

    /// <summary>
    /// Wrap <paramref name="value"/> as a bytes-format payload through <see cref="JobPayload.Bytes"/>
    /// and set the input. Empty array is allowed.
    /// </summary>
    public JobRequestBuilder Bytes(byte[] value) => Payload(JobPayload.Bytes(value));

    /// <summary>
    /// Reset the input to <see cref="JobPayload.None"/>. Useful after a prior payload setter when
    /// the caller decides the job is no-payload.
    /// </summary>
    public JobRequestBuilder NoPayload() => Payload(JobPayload.None);

    /// <summary>
    /// Deduplicate this job definition using the caller's <paramref name="businessKey"/>. This is the
    /// primary raw-builder API and composes
    /// <c>&lt;jobName-from-Create&gt;:&lt;businessKey&gt;</c> before assigning the final key.
    /// </summary>
    public JobRequestBuilder Deduplicate(string businessKey) =>
        DeduplicationKey(Acta.DeduplicationKey.ForDefinition(_jobName, businessKey));

    /// <summary>
    /// Assign an already composed final deduplication key. This is the lower-level API for explicit
    /// cross-definition or time-bucketed keys; prefer <see cref="Deduplicate"/> for the usual
    /// definition-scoped business key.
    /// </summary>
    public JobRequestBuilder DeduplicationKey(string deduplicationKey)
    {
        deduplicationKey = IdentifierSyntax.NormalizeKey(deduplicationKey, nameof(deduplicationKey));
        _deduplicationKey = deduplicationKey;
        return this;
    }

    /// <summary>
    /// Set the correlation id threading related jobs together: a W3C trace id or a caller's own custom
    /// value, at most 64 chars.
    /// </summary>
    public JobRequestBuilder CorrelationKey(string correlationKey)
    {
        IdentifierSyntax.ValidateExternalToken(correlationKey, nameof(correlationKey), IdentifierSyntax.DefaultMaxLength);
        _correlationKey = correlationKey;
        return this;
    }

    /// <summary>
    /// Set the exclusive key: at most one Job per <c>(namespace, exclusive key)</c> executes at a
    /// time (the worker takes a namespace-scoped lock after claim, before the handler; a job whose
    /// key is held returns to Ready after a short delay). Mutual exclusion only, no per-key
    /// ordering. Not calling this leaves the Job unconstrained.
    /// </summary>
    public JobRequestBuilder ExclusiveKey(string exclusiveKey)
    {
        exclusiveKey = IdentifierSyntax.NormalizeKey(exclusiveKey, nameof(exclusiveKey));
        _exclusiveKey = exclusiveKey;
        return this;
    }

    /// <summary>
    /// Override the job-definition's policy priority for this row only. <c>null</c> via not
    /// calling <see cref="Priority"/> falls back to the definition's effective <c>priority_code</c>.
    /// </summary>
    public JobRequestBuilder Priority(JobPriorityCode priority)
    {
        _priority = priority;
        return this;
    }

    /// <summary>
    /// Hold the earliest claim of the Job until the absolute instant <paramref name="utc"/> (delayed
    /// enqueue), the explicit fixed-wall-clock-time path. Normalized to UTC. Not calling this leaves
    /// the Job claimable immediately. Last-call-wins with <see cref="Delayed"/>, which it clears.
    /// </summary>
    public JobRequestBuilder NextExecutionAt(DateTimeOffset utc)
    {
        _nextRunAtUtc = utc.UtcDateTime;
        _delaySeconds = null;
        return this;
    }

    /// <summary>
    /// Delay the earliest claim of the Job by <paramref name="delay"/>, resolved against the database
    /// clock (<c>db_now + delay</c>) at enqueue so the caller's clock never affects scheduling.
    /// <paramref name="delay"/> must be non-negative; rounded up to whole seconds. Last-call-wins with
    /// <see cref="NextExecutionAt"/>, which it clears.
    /// </summary>
    public JobRequestBuilder Delayed(TimeSpan delay)
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
    /// Enqueue as a child of the given Job. The parent must exist and be non-terminal; the child
    /// inherits the parent's lineage root and (when unset) its correlation id and tenant, and
    /// <c>DeduplicationKey</c> dedup becomes sibling-unique per parent.
    /// </summary>
    public JobRequestBuilder ParentId(long parentJobId)
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
    public JobRequestBuilder TenantKey(string tenantKey, bool overrideParent = false)
    {
        _tenantKey = IdentifierSyntax.NormalizeTenantKey(tenantKey, nameof(tenantKey));
        _overrideParentTenant = overrideParent;
        return this;
    }

    /// <summary>
    /// Convenience overload for the common case of stamping a Job with a batch identifier.
    /// Equivalent to <c>Tag("batch", batchId)</c>; both paths converge on the same tag.
    /// </summary>
    public JobRequestBuilder Batch(string? batchId = null) => Tag("batch", batchId);

    /// <summary>
    /// Add or replace a single tag. Names follow dotted-kebab (`env.prod`, `com.acme.tier`),
    /// rejecting the <c>sys.</c> system prefix. Same-name calls follow last-write-wins.
    /// </summary>
    public JobRequestBuilder Tag(string name, string? value = null)
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
    /// Add (or replace) one or more tags in a single call. Each entry follows the same validation
    /// rules as <see cref="Tag"/>; duplicates against existing tags (or within the same
    /// <paramref name="tags"/> array) follow last-write-wins.
    /// </summary>
    public JobRequestBuilder Tags(params TagInput[] tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        foreach (var tag in tags)
        {
            if (tag is null)
            {
                throw new ArgumentException("Tag entries must not be null.", nameof(tags));
            }
            Tag(tag.Name, tag.Value);
        }
        return this;
    }

    /// <summary>
    /// Snapshot the current builder state into a fresh <see cref="JobEnqueueRequest"/>. Safe to
    /// call repeatedly; subsequent builder mutation does not affect prior results.
    /// </summary>
    public JobEnqueueRequest Build()
    {
        return new JobEnqueueRequest(
            JobNamespace: _jobNamespace,
            JobName: _jobName,
            Input: _input,
            DeduplicationKey: _deduplicationKey,
            CorrelationKey: _correlationKey,
            Priority: _priority
        )
        {
            Tags = SnapshotTags(),
            ExclusiveKey = _exclusiveKey,
            NextRunAtUtc = _nextRunAtUtc,
            DelaySeconds = _delaySeconds,
            ParentId = _parentId,
            TenantKey = _tenantKey,
            OverrideParentTenant = _overrideParentTenant,
        };
    }

    private IReadOnlyList<TagInput>? SnapshotTags()
    {
        if (_tags.Count == 0)
        {
            return null;
        }
        var snapshot = new TagInput[_tags.Count];
        var i = 0;
        foreach (var tag in _tags.Values)
        {
            snapshot[i++] = tag;
        }
        return snapshot;
    }
}
