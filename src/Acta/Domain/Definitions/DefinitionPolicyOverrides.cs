using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// The whole set of operator overrides of a definition's policy, read and written as a unit: a member with a value overrides what the
/// code declares, and null leaves the declared value in force. A request carries every member, null where there is no override. A
/// deadline override on a scheduled definition is ignored, and a rate limit applies to every definition that shares the meter.
/// </summary>
/// <param name="Priority">The priority to use in place of the declared one; null uses the declared one.</param>
/// <param name="MaxAttempts">The retry budget to use in place of the declared one; null uses the declared one.</param>
/// <param name="ConcurrencyLimit">The concurrency limit to use in place of the declared one, 1 to 1024; null uses the declared
/// one.</param>
/// <param name="RateLimit">The rate limit to use in place of the declared one, written N/s, N/m, or N/h; null uses the declared
/// one.</param>
/// <param name="Backoff">The backoff to use in place of the declared one, as an expression such as 1m..8h x2 ~10%: the first retry
/// waits 1m, each next one twice as long up to 8h, and each delay moves at random by up to 10% either way. Null uses the declared
/// one.</param>
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
    [property: JsonRequired] JobPriorityCode? Priority = null,
    [property: JsonRequired] int? MaxAttempts = null,
    [property: JsonRequired] int? ConcurrencyLimit = null,
    [property: JsonRequired] string? RateLimit = null,
    [property: JsonRequired] string? Backoff = null,
    [property: JsonRequired] int? ExecutionTimeoutSeconds = null,
    [property: JsonRequired] int? DeadlineSeconds = null,
    [property: JsonRequired] DeadlineBehaviorCode? DeadlineBehavior = null,
    [property: JsonRequired] int? JobRetentionSeconds = null,
    [property: JsonRequired] EventRecordingCode? EventRecording = null,
    [property: JsonRequired] AlertProfileCode? AlertProfile = null,
    [property: JsonRequired] string? AlertChannelName = null,
    [property: JsonRequired] string? RunbookUrl = null,
    [property: JsonRequired] string? DisplayName = null,
    [property: JsonRequired] string? Description = null
);
