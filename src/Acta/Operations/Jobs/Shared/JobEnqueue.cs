using System.Data.Common;

namespace Acta;

/// <summary>
/// What the two enqueue handlers share: canonicalizing and validating a row, translating the routines' guard
/// rejections, and the wake rule.
/// </summary>
internal static class JobEnqueue
{
    /// <summary>
    /// Normalizes and validates one request, caps its payload, and canonicalizes it into the row the routine
    /// takes; <paramref name="index"/> names the row in a batch's messages.
    /// </summary>
    public static (EnqueueJobRequest Request, JobEnqueueValues Row) Prepare(
        EnqueueJobRequest request,
        string paramName,
        int index,
        int maxInlinePayloadBytes
    )
    {
        request = JobEnqueueRequestValidation.NormalizeAndValidate(request, paramName);
        if (request.Input.Data.Length > maxInlinePayloadBytes)
        {
            throw new PayloadTooLargeException("enqueue input", request.Input.Data.Length, maxInlinePayloadBytes);
        }

        var row = Canonicalize(
            new JobEnqueueValues(
                NamespaceName: request.JobNamespace,
                JobName: request.JobName,
                Input: request.Input,
                PriorityOverride: request.Priority,
                DeduplicationKey: request.DeduplicationKey,
                CorrelationKey: request.CorrelationKey,
                Tags: request.Tags,
                ConcurrencyKey: request.ConcurrencyKey,
                NextRunAtUtc: request.NextRunAtUtc,
                DelaySeconds: request.DelaySeconds,
                ParentJobId: request.ParentJobId,
                TenantName: request.TenantName,
                OverrideParentTenant: request.OverrideParentTenant,
                Lane: request.Lane
            )
        );
        ValidateRow(row, index);
        return (request, row);
    }

    /// <summary>
    /// Names fold to lowercase outside the <c>sys</c> reservation; keys keep their case, and the deduplication key may
    /// not start with <c>sys.</c>.
    /// </summary>
    internal static JobEnqueueValues Canonicalize(JobEnqueueValues row)
    {
        IReadOnlyList<TagInput>? tags = row.Tags?.Select((t, i) => TagInput.Normalize(t, $"Tags[{i}]")).ToList();

        return row with
        {
            NamespaceName = NameSyntax.Canonicalize(row.NamespaceName, nameof(row.NamespaceName), reserveSystem: true),
            JobName = NameSyntax.Canonicalize(row.JobName, nameof(row.JobName), NameSyntax.ExtendedMaxLength, reserveSystem: true),
            DeduplicationKey = row.DeduplicationKey is null
                ? null
                : DeduplicationKey.Canonicalize(row.DeduplicationKey, nameof(row.DeduplicationKey)),
            CorrelationKey = row.CorrelationKey is null
                ? null
                : KeySyntax.Canonicalize(row.CorrelationKey, nameof(row.CorrelationKey), NameSyntax.DefaultMaxLength),
            ConcurrencyKey = row.ConcurrencyKey is null ? null : KeySyntax.Canonicalize(row.ConcurrencyKey, nameof(row.ConcurrencyKey)),
            TenantName = row.TenantName is null
                ? null
                : NameSyntax.Canonicalize(row.TenantName, nameof(row.TenantName), NameSyntax.ExtendedMaxLength, reserveSystem: true),
            Lane = row.Lane is null
                ? null
                : NameSyntax.Canonicalize(row.Lane, nameof(row.Lane), NameSyntax.ExtendedMaxLength, reserveSystem: true),
            Tags = tags,
        };
    }

    /// <summary>
    /// The absolute (NextRunAtUtc) and relative (DelaySeconds) delayed-enqueue channels are
    /// mutually exclusive; the SQL COALESCE would silently prefer the absolute one, so reject the
    /// ambiguity here rather than pick a winner. A negative delay is meaningless (the routine
    /// clamps null to immediate).
    /// </summary>
    internal static void ValidateRow(JobEnqueueValues row, int index)
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

        if (row.ParentJobId is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(row),
                row.ParentJobId,
                $"Enqueue row at index {index} has a non-positive ParentJobId."
            );
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

    /// <summary>
    /// Runs the routine, turning a guard's ACTA:ENQ_* rejection into <see cref="EnqueueRejectedException"/> and a parent
    /// fence's refusal into <see cref="ExecutionFencedException"/>. SQLite RAISE carries only text, so the token lives in
    /// the message, matched anywhere because provider wrappers prepend context such as "SQLite Error N:".
    /// </summary>
    public static async Task<IReadOnlyList<TRow>> ExecuteAsync<TRow>(Func<Task<IReadOnlyList<TRow>>> execute)
    {
        try
        {
            return await execute();
        }
        catch (DbException ex) when (Rejection(ex) is { } rejected)
        {
            throw rejected;
        }
        catch (DbException ex) when (ExecutionFencedException.From(ex) is { } fenced)
        {
            throw fenced;
        }
    }

    private static readonly (string Token, EnqueueRejectionReason Reason)[] RejectionTokens =
    [
        ("ACTA:ENQ_NS_SUSPENDED:", EnqueueRejectionReason.NamespaceSuspended),
        ("ACTA:ENQ_TENANT_SUSPENDED:", EnqueueRejectionReason.TenantSuspended),
        ("ACTA:ENQ_TENANT_UNKNOWN:", EnqueueRejectionReason.TenantUnknown),
        ("ACTA:ENQ_ROUTE_UNKNOWN:", EnqueueRejectionReason.RouteUnknown),
        ("ACTA:ENQ_DEF_RETIRED:", EnqueueRejectionReason.DefinitionRetired),
        ("ACTA:ENQ_TENANT_REQUIRED:", EnqueueRejectionReason.TenantRequired),
        ("ACTA:ENQ_TENANT_FORBIDDEN:", EnqueueRejectionReason.TenantForbidden),
        ("ACTA:ENQ_TENANT_MISMATCH:", EnqueueRejectionReason.TenantMismatch),
        ("ACTA:ENQ_ANCESTOR_LANE:", EnqueueRejectionReason.AncestorLane),
        ("ACTA:ENQ_LANE_ISOLATION:", EnqueueRejectionReason.LaneIsolation),
    ];

    private static EnqueueRejectedException? Rejection(DbException ex)
    {
        foreach (var (token, reason) in RejectionTokens)
        {
            var idx = ex.Message.IndexOf(token, StringComparison.Ordinal);
            if (idx >= 0)
            {
                return new EnqueueRejectedException(reason, ex.Message[(idx + token.Length)..], ex);
            }
        }
        return null;
    }

    /// <summary>
    /// The publish rule: a request scheduled ahead of now (a positive delay or any absolute run time) wakes
    /// HorizonChanged when inserted and nothing when deduplicated; a due-now request wakes WorkAvailable either way.
    /// </summary>
    public static WorkerWakeupReason? WakeReason(EnqueueJobRequest request, EnqueueJobAction action)
    {
        var scheduledAhead = request.DelaySeconds is > 0 || request.NextRunAtUtc is not null;
        return !scheduledAhead ? WorkerWakeupReason.WorkAvailable
            : action == EnqueueJobAction.Inserted ? WorkerWakeupReason.HorizonChanged
            : null;
    }
}
