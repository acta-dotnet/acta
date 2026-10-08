namespace Acta.Runtime;

/// <summary>
/// Overlays a definition's effective (override-or-default) policy onto its code-built <see cref="JobDescriptor"/>: the
/// descriptor keeps its handler, contract, and invocation metadata, and only the operator-overridable policy fields
/// are replaced with the DB-computed effective values. The worker holds the overlaid descriptors in
/// <c>WorkerContext.DescriptorByDefinitionId</c> as its live, reloadable policy view, so the hot path, which reads the
/// descriptor rather than the DB, honors operator overrides. <c>RateKey</c> is absent on purpose: code-owned, with no
/// override, so the declared value is already the effective one.
/// </summary>
internal static class EffectivePolicyOverlay
{
    public static JobDescriptor Apply(JobDescriptor descriptor, EffectiveJobPolicy p) =>
        descriptor with
        {
            Priority = p.Priority,
            MaxAttempts = p.MaxAttempts,
            ConcurrencyLimit = p.ConcurrencyLimit,
            RateLimit = p.RateLimit,
            AuditLevel = p.AuditLevel,
            AlertProfile = p.AlertProfile,
            Backoff = p.Backoff,
            ExecutionTimeoutSeconds = p.ExecutionTimeoutSeconds,
            DeadlineSeconds = p.DeadlineSeconds,
            DeadlineBehavior = p.DeadlineBehavior,
            JobRetentionSeconds = p.JobRetentionSeconds,
            AlertChannelName = p.AlertChannelName,
            RunbookUrl = p.RunbookUrl,
        };
}
