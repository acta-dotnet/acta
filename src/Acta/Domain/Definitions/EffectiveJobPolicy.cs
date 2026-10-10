namespace Acta;

/// <summary>
/// A definition's effective (override-or-default) policy, read straight off the DB-computed
/// <c>*_effective</c> generated columns. The worker overlays this onto its in-memory
/// <see cref="JobDescriptor"/> index so the execution hot path (which reads the descriptor, not the DB)
/// honors operator overrides.
/// </summary>
internal sealed record EffectiveJobPolicy(
    JobPriorityCode Priority,
    int MaxAttempts,
    int? ConcurrencyLimit,
    string? RateLimit,
    string? RateName,
    string Backoff,
    int ExecutionTimeoutSeconds,
    int DeadlineSeconds,
    DeadlineBehaviorCode DeadlineBehavior,
    int JobRetentionSeconds,
    DefinitionAuditLevelCode AuditLevel,
    AlertProfileCode AlertProfile,
    string? AlertChannelName,
    string? RunbookUrl
);
