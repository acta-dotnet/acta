using Acta.Features.Definitions;
using Acta.Features.Workers;

namespace Acta.Features.Definitions;

/// <summary>
/// Overlays a definition's effective (override-or-default) policy onto its code-built
/// <see cref="JobDescriptor"/>. The descriptor keeps its handler, contract, and invocation metadata;
/// only the operator-overridable policy fields are replaced with the DB-computed effective values. The
/// worker holds the overlaid descriptors in <c>WorkerContext.DescriptorByDefinitionId</c> as its live,
/// reloadable policy view, so the execution hot path (which reads the descriptor, not the DB) honors
/// operator overrides.
/// </summary>
internal static class EffectivePolicyOverlay
{
    public static JobDescriptor Apply(JobDescriptor descriptor, EffectiveJobPolicy p) =>
        descriptor with
        {
            Priority = p.Priority,
            MaxAttempts = p.MaxAttempts,
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
