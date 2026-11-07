using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// One definition: its identity and status, the types and formats of its input and result, and each policy as the value the code
/// declares, the operator's override, null when there is none, and the value in force. A definition is addressed by its namespace and
/// name.
/// </summary>
/// <param name="DefinitionId">The definition's internal id; the JSON omits it.</param>
/// <param name="JobNamespace">The definition's namespace.</param>
/// <param name="JobName">The definition's name within its namespace.</param>
/// <param name="Status">The definition's status.</param>
/// <param name="DefinitionHash">A hash of everything the code declares for the definition; operator overrides are not part of it.</param>
/// <param name="ManifestGenerationAtUtc">The build time of the deployment that last wrote the definition, in UTC; an older deployment
/// cannot overwrite it.</param>
/// <param name="InputTypeName">The full name of the .NET type the handler takes as input.</param>
/// <param name="InputFormatId">The id of the payload format the input is stored in.</param>
/// <param name="InputFormatName">The name of the payload format the input is stored in.</param>
/// <param name="ResultTypeName">The full name of the .NET type the handler returns; null when it returns nothing.</param>
/// <param name="ResultFormatId">The id of the payload format the result is stored in.</param>
/// <param name="ResultFormatName">The name of the payload format the result is stored in.</param>
/// <param name="Priority">The priority the code declares for the definition's jobs.</param>
/// <param name="PriorityOverride">The operator's override of the priority; null when there is none.</param>
/// <param name="PriorityEffective">The priority in force: the override when there is one, else the declared one.</param>
/// <param name="MaxAttempts">The declared retry budget: how many attempts in a row may fail before a job ends failed.</param>
/// <param name="MaxAttemptsOverride">The operator's override of the retry budget; null when there is none.</param>
/// <param name="MaxAttemptsEffective">The retry budget in force: the override when there is one, else the declared one.</param>
/// <param name="ConcurrencyLimit">The declared concurrency limit: how many attempts of the definition may run at once, 1 to 1024; null
/// declares none.</param>
/// <param name="ConcurrencyLimitOverride">The operator's override of the concurrency limit; null when there is none.</param>
/// <param name="ConcurrencyLimitEffective">The concurrency limit in force: the override when there is one, else the declared one; null
/// when neither sets one.</param>
/// <param name="RateLimit">The declared rate limit: how often attempts may start, written N/s, N/m, or N/h; null declares none.</param>
/// <param name="RateLimitOverride">The operator's override of the rate limit; null when there is none.</param>
/// <param name="RateLimitEffective">The rate limit in force: the override when there is one, else the declared one; null when neither
/// sets one.</param>
/// <param name="RateName">The meter the rate limit spends from, shared by every definition that names it; null means the definition's own
/// name.</param>
/// <param name="Lane">The lane the definition's jobs run in, when the code declares one.</param>
/// <param name="Backoff">The declared retry backoff, as an expression such as 1m..8h x2 ~10%.</param>
/// <param name="BackoffOverride">The operator's override of the backoff; null when there is none.</param>
/// <param name="BackoffEffective">The backoff in force: the override when there is one, else the declared one.</param>
/// <param name="ExecutionTimeoutSeconds">The declared limit on one attempt's run time, in seconds; past it the handler's cancellation
/// token fires.</param>
/// <param name="ExecutionTimeoutSecondsOverride">The operator's override of the execution timeout; null when there is none.</param>
/// <param name="ExecutionTimeoutSecondsEffective">The execution timeout in force: the override when there is one, else the declared
/// one.</param>
/// <param name="DeadlineSeconds">The declared deadline for a whole job, in seconds from its creation; 0 declares none.</param>
/// <param name="DeadlineSecondsOverride">The operator's override of the deadline; null when there is none.</param>
/// <param name="DeadlineSecondsEffective">The deadline in force: the override when there is one, else the declared one.</param>
/// <param name="DeadlineBehavior">What the code declares happens to a job past its deadline.</param>
/// <param name="DeadlineBehaviorOverride">The operator's override of the deadline behavior; null when there is none.</param>
/// <param name="DeadlineBehaviorEffective">The deadline behavior in force: the override when there is one, else the declared one.</param>
/// <param name="JobRetentionSeconds">The declared time a finished job is kept before retention deletes it, in seconds.</param>
/// <param name="JobRetentionSecondsOverride">The operator's override of the retention time; null when there is none.</param>
/// <param name="JobRetentionSecondsEffective">The retention time in force: the override when there is one, else the declared one.</param>
/// <param name="EventRecording">How much of a job's history the code declares is recorded as events.</param>
/// <param name="EventRecordingOverride">The operator's override of event recording; null when there is none.</param>
/// <param name="EventRecordingEffective">The event recording in force: the override when there is one, else the declared one.</param>
/// <param name="AlertProfile">When, as the code declares it, the engine raises alerts for the definition's jobs on its own.</param>
/// <param name="AlertProfileOverride">The operator's override of the alert profile; null when there is none.</param>
/// <param name="AlertProfileEffective">The alert profile in force: the override when there is one, else the declared one.</param>
/// <param name="FiringFailurePolicy">What a firing that fails for good does to the definition's schedules: they continue, or scheduling
/// pauses.</param>
/// <param name="ScheduledDelivery">How schedules deliver the definition's work: by firing its one permanent slot job again and again, or
/// by enqueueing a new job for each occurrence.</param>
/// <param name="AlertChannelName">The alert channel the code declares for the definition's automatic alerts.</param>
/// <param name="AlertChannelNameOverride">The operator's override of the alert channel; null when there is none.</param>
/// <param name="AlertChannelNameEffective">The alert channel in force: the override when there is one, else the declared one.</param>
/// <param name="RunbookUrl">The runbook link the code declares, shown on the definition's alerts and in the dashboard.</param>
/// <param name="RunbookUrlOverride">The operator's override of the runbook link; null when there is none.</param>
/// <param name="RunbookUrlEffective">The runbook link in force: the override when there is one, else the declared one.</param>
/// <param name="DisplayName">The display name the code declares for the definition.</param>
/// <param name="DisplayNameOverride">The operator's override of the display name; null when there is none.</param>
/// <param name="DisplayNameEffective">The display name in force: the override when there is one, else the declared one.</param>
/// <param name="Description">The description the code declares for the definition.</param>
/// <param name="DescriptionOverride">The operator's override of the description; null when there is none.</param>
/// <param name="DescriptionEffective">The description in force: the override when there is one, else the declared one.</param>
/// <param name="CreatedAtUtc">When the row was created, in UTC.</param>
/// <param name="ModifiedAtUtc">When the row last changed, in UTC.</param>
/// <param name="Version">The definition's version, which every change to it increments; send it as the expected version of a
/// control.</param>
public sealed record GetDefinitionResponse(
    [property: JsonIgnore] int DefinitionId,
    string JobNamespace,
    string JobName,
    DefinitionStatusCode Status,
    string DefinitionHash,
    DateTime ManifestGenerationAtUtc,
    string InputTypeName,
    byte InputFormatId,
    string InputFormatName,
    string? ResultTypeName,
    byte ResultFormatId,
    string ResultFormatName,
    JobPriorityCode Priority,
    JobPriorityCode? PriorityOverride,
    JobPriorityCode PriorityEffective,
    int MaxAttempts,
    int? MaxAttemptsOverride,
    int MaxAttemptsEffective,
    int? ConcurrencyLimit,
    int? ConcurrencyLimitOverride,
    int? ConcurrencyLimitEffective,
    string? RateLimit,
    string? RateLimitOverride,
    string? RateLimitEffective,
    string? RateName,
    string? Lane,
    string Backoff,
    string? BackoffOverride,
    string BackoffEffective,
    int ExecutionTimeoutSeconds,
    int? ExecutionTimeoutSecondsOverride,
    int ExecutionTimeoutSecondsEffective,
    int DeadlineSeconds,
    int? DeadlineSecondsOverride,
    int DeadlineSecondsEffective,
    DeadlineBehaviorCode DeadlineBehavior,
    DeadlineBehaviorCode? DeadlineBehaviorOverride,
    DeadlineBehaviorCode DeadlineBehaviorEffective,
    int JobRetentionSeconds,
    int? JobRetentionSecondsOverride,
    int JobRetentionSecondsEffective,
    EventRecordingCode EventRecording,
    EventRecordingCode? EventRecordingOverride,
    EventRecordingCode EventRecordingEffective,
    AlertProfileCode AlertProfile,
    AlertProfileCode? AlertProfileOverride,
    AlertProfileCode AlertProfileEffective,
    FiringFailurePolicyCode FiringFailurePolicy,
    ScheduledDeliveryCode ScheduledDelivery,
    string? AlertChannelName,
    string? AlertChannelNameOverride,
    string? AlertChannelNameEffective,
    string? RunbookUrl,
    string? RunbookUrlOverride,
    string? RunbookUrlEffective,
    string? DisplayName,
    string? DisplayNameOverride,
    string? DisplayNameEffective,
    string? Description,
    string? DescriptionOverride,
    string? DescriptionEffective,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc,
    int Version
);
