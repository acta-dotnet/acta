namespace Acta.Runtime.Modules.Execution.Jobs;

/// <summary>
/// Caller-supplied per-row enqueue request, shared by the scalar <c>EnqueueOne</c> and the
/// batched <c>EnqueueBatch</c> operations.
/// </summary>
/// <remarks>
/// <paramref name="ExclusiveKey"/> is a named mutual-exclusion key (null is unconstrained): at most
/// one Job per <c>(JobNamespace, ExclusiveKey)</c> executes at a time. The worker enforces this at
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
    string? ExclusiveKey = null,
    DateTime? NextRunAtUtc = null,
    int? DelaySeconds = null,
    IReadOnlyList<TagInput>? Tags = null,
    long? ParentId = null,
    string? TenantKey = null,
    bool OverrideParentTenant = false
);

/// <summary>
/// The (ordinal, id, ref, action) outcome row both enqueue routines return; the scalar routine
/// always returns ordinal 0.
/// </summary>
internal readonly record struct EnqueueOutcomeRow(int Ordinal, long JobId, Guid JobRef, JobEnqueueAction Action);

/// <summary>
/// Per-row canonicalization and validation shared by <c>EnqueueOne</c> and
/// <c>EnqueueBatch</c>; cross-row (batch-only) validation stays in <c>EnqueueBatch</c>.
/// </summary>
internal static class JobEnqueueRows
{
    // Names must already be lowercase Acta names; equality keys normalize to lowercase ASCII.
    // CorrelationKey is an external token and is preserved exactly.
    internal static JobEnqueueRow Canonicalize(JobEnqueueRow row)
    {
        IReadOnlyList<TagInput>? tags = row.Tags?.Select((t, i) => TagInput.Normalize(t, $"Tags[{i}]")).ToList();

        return row with
        {
            NamespaceName = IdentifierSyntax.CanonicalizeUserKebab(row.NamespaceName, nameof(row.NamespaceName)),
            JobName = IdentifierSyntax.CanonicalizeUserKebab(row.JobName, nameof(row.JobName), IdentifierSyntax.ExtendedMaxLength),
            DeduplicationKey = row.DeduplicationKey is null
                ? null
                : IdentifierSyntax.NormalizeKey(row.DeduplicationKey, nameof(row.DeduplicationKey)),
            ExclusiveKey = row.ExclusiveKey is null ? null : IdentifierSyntax.NormalizeKey(row.ExclusiveKey, nameof(row.ExclusiveKey)),
            TenantKey = row.TenantKey is null ? null : IdentifierSyntax.NormalizeTenantKey(row.TenantKey, nameof(row.TenantKey)),
            Tags = tags,
        };
    }

    // The absolute (NextRunAtUtc) and relative (DelaySeconds) delayed-enqueue channels are mutually
    // exclusive; the SQL COALESCE would silently prefer the absolute one, so reject the ambiguity here
    // rather than pick a winner. A negative delay is meaningless (the routine clamps null to immediate).
    internal static void ValidateRow(JobEnqueueRow row, int index)
    {
        if (row.NextRunAtUtc is not null && row.DelaySeconds is not null)
        {
            throw new ArgumentException(
                $"Enqueue row at index {index} sets both NextRunAtUtc and DelaySeconds. "
                    + "Use an absolute instant or a relative delay, not both.",
                nameof(row)
            );
        }

        if (row.DelaySeconds is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(row),
                row.DelaySeconds,
                $"Enqueue row at index {index} has a negative DelaySeconds."
            );
        }

        if (row.ParentId is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(row), row.ParentId, $"Enqueue row at index {index} has a non-positive ParentId.");
        }

        if (row.Tags is { Count: > 1 } tags)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                if (!seen.Add(tag.Name))
                {
                    throw new ArgumentException(
                        $"Enqueue row at index {index} has duplicate tag name '{tag.Name}'. "
                            + "tags's (job_id, name) PK requires unique names per job.",
                        nameof(row)
                    );
                }
            }
        }
    }
}
