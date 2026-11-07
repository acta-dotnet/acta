namespace Acta;

/// <summary>
/// The whole set of operator overrides of a definition's policy, read and written as a unit: a member with a value overrides what the
/// code declares, and null leaves the declared value in force. A deadline override on a scheduled definition is ignored, and a rate limit
/// applies to every definition that shares the meter.
/// </summary>
/// <param name="Priority">The priority to use in place of the declared one; null uses the declared one.</param>
/// <param name="MaxAttempts">The retry budget to use in place of the declared one; null uses the declared one.</param>
/// <param name="ConcurrencyLimit">The concurrency limit to use in place of the declared one, 1 to 1024; null uses the declared
/// one.</param>
/// <param name="RateLimit">The rate limit to use in place of the declared one, written N/s, N/m, or N/h; null uses the declared
/// one.</param>
/// <param name="Backoff">The backoff expression to use in place of the declared one; null uses the declared one.</param>
/// <param name="ExecutionTimeoutSeconds">The execution timeout in seconds to use in place of the declared one; null uses the declared
/// one.</param>
/// <param name="DeadlineSeconds">The deadline in seconds to use in place of the declared one; null uses the declared one.</param>
/// <param name="DeadlineBehavior">The deadline behavior to use in place of the declared one; null uses the declared one.</param>
/// <param name="JobRetentionSeconds">The retention time in seconds to use in place of the declared one; null uses the declared
/// one.</param>
/// <param name="EventRecording">The event recording to use in place of the declared one; null uses the declared one.</param>
/// <param name="AlertProfile">The alert profile to use in place of the declared one; null uses the declared one.</param>
/// <param name="AlertChannelName">The alert channel to use in place of the declared one; null uses the declared one.</param>
/// <param name="RunbookUrl">The runbook link to use in place of the declared one; null uses the declared one.</param>
/// <param name="DisplayName">The display name to use in place of the declared one; null uses the declared one.</param>
/// <param name="Description">The description to use in place of the declared one; null uses the declared one.</param>
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
    EventRecordingCode? EventRecording = null,
    AlertProfileCode? AlertProfile = null,
    string? AlertChannelName = null,
    string? RunbookUrl = null,
    string? DisplayName = null,
    string? Description = null
);
