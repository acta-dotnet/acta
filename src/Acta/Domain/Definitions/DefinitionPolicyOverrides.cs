namespace Acta;

/// <summary>
/// The whole operator-override set for a definition's policy, applied as a unit: a non-null field sets its override
/// and null clears it back to the code default, so the dashboard reads, edits, and writes the full set. Code defaults,
/// contract, formats, and <c>definition_hash</c> are never touched. A <c>DeadlineSeconds</c> override on a scheduled
/// definition lands but is ignored: a deadline anchors to job creation, and a recurring slot lives forever.
/// <c>RateLimit</c> applies to every definition sharing the meter (declared <c>RateName</c>, or the name) in one
/// write, clearing included, so the meter never carries two rates.
/// </summary>
public sealed record DefinitionPolicyOverrides(
    JobPriorityCode? Priority = null,
    int? MaxAttempts = null,
    int? ConcurrencyLimit = null,
    string? RateLimit = null,
    string? Backoff = null,
    int? ExecutionTimeoutSeconds = null,
    int? DeadlineSeconds = null,
    DeadlineBehaviorCode? DeadlineBehavior = null,
    int? JobRetentionSeconds = null,
    DefinitionAuditLevelCode? AuditLevel = null,
    AlertProfileCode? AlertProfile = null,
    string? AlertChannelName = null,
    string? RunbookUrl = null,
    string? DisplayName = null,
    string? Description = null
);
