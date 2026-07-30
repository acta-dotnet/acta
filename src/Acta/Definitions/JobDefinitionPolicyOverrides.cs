namespace Acta;

/// <summary>
/// The complete operator-override set applied to a job definition's policy. Each field is the override
/// for its policy slot: a non-null value sets the override, <c>null</c> clears it (the effective value
/// falls back to the code default). Applied as a whole - the supplied set replaces the row's current
/// overrides - so the dashboard reads the current overrides, edits, and writes the full set back. Never
/// touches the code-owned defaults, the contract/formats, or <c>definition_hash</c>.
/// </summary>
public sealed record JobDefinitionPolicyOverrides(
    JobPriorityCode? Priority = null,
    short? MaxAttempts = null,
    string? Backoff = null,
    int? ExecutionTimeoutSeconds = null,
    int? DeadlineSeconds = null,
    DeadlineBehaviorCode? DeadlineBehavior = null,
    int? JobRetentionSeconds = null,
    JobAuditLevelCode? AuditLevel = null,
    JobAlertProfileCode? AlertProfile = null,
    string? AlertChannelName = null,
    string? RunbookUrl = null,
    string? DisplayName = null,
    string? Description = null
);
