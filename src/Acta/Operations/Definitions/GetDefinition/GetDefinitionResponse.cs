using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// One definition: its identity and status, the types and formats of its input and result, each policy as the value the code
/// declares and the value in force, and the operator's overrides of them. A definition is addressed by its namespace and name.
/// </summary>
/// <param name="DefinitionId">The definition's internal id; the JSON omits it.</param>
/// <param name="JobNamespace">The definition's namespace.</param>
/// <param name="JobName">The definition's name within its namespace.</param>
/// <param name="Status">The definition's status.</param>
/// <param name="DefinitionHash">A hash of everything the code declares for the definition; operator overrides are not part of it.</param>
/// <param name="InputTypeName">The full name of the type the handler takes as input, as its code spells it.</param>
/// <param name="InputFormatId">The id of the payload format the input is stored in.</param>
/// <param name="InputFormatName">The name of the payload format the input is stored in.</param>
/// <param name="ResultTypeName">The full name of the type the handler returns, as its code spells it; null when it returns
/// nothing.</param>
/// <param name="ResultFormatId">The id of the payload format the result is stored in.</param>
/// <param name="ResultFormatName">The name of the payload format the result is stored in.</param>
/// <param name="Priority">The priority the code declares for the definition's jobs.</param>
/// <param name="PriorityEffective">The priority in force: the override when there is one, else the declared one.</param>
/// <param name="MaxAttempts">The declared retry budget: how many attempts in a row may fail before a job ends failed.</param>
/// <param name="MaxAttemptsEffective">The retry budget in force: the override when there is one, else the declared one.</param>
/// <param name="ConcurrencyLimit">The declared concurrency limit: how many attempts of the definition may run at once, 1 to 1024; null
/// declares none.</param>
/// <param name="ConcurrencyLimitEffective">The concurrency limit in force: the override when there is one, else the declared one; null
/// when neither sets one.</param>
/// <param name="RateLimit">The declared rate limit, written N/s, N/m, or N/h: it paces how often attempts are admitted, not when they
/// start, and is no hard cap on a burst, since a turn admitted late can land beside an on-time one. Null declares none.</param>
/// <param name="RateLimitEffective">The rate limit in force: the override when there is one, else the declared one; null when neither
/// sets one.</param>
/// <param name="RateName">The meter the rate limit spends from, shared by every definition that names it; null means the definition's own
/// name.</param>
/// <param name="Lane">The lane the definition's jobs run in, when the code declares one.</param>
/// <param name="Backoff">The declared retry backoff, as an expression such as 1m..8h x2 ~10%: the first retry waits 1m, each next one
/// twice as long up to 8h, and each delay moves at random by up to 10% either way.</param>
/// <param name="BackoffEffective">The backoff in force: the override when there is one, else the declared one.</param>
/// <param name="ExecutionTimeoutSeconds">The declared limit on one attempt's run time, in seconds; past it the handler's cancellation
/// token fires.</param>
/// <param name="ExecutionTimeoutSecondsEffective">The execution timeout in force: the override when there is one, else the declared
/// one.</param>
/// <param name="DeadlineSeconds">The declared deadline for a whole job, in seconds from its creation; 0 declares none.</param>
/// <param name="DeadlineSecondsEffective">The deadline in force: the override when there is one, else the declared one.</param>
/// <param name="DeadlineBehavior">What the code declares happens to a job past its deadline.</param>
/// <param name="DeadlineBehaviorEffective">The deadline behavior in force: the override when there is one, else the declared one.</param>
/// <param name="JobRetentionSeconds">The declared time a finished job is kept before retention deletes it, in seconds.</param>
/// <param name="JobRetentionSecondsEffective">The retention time in force: the override when there is one, else the declared one.</param>
/// <param name="EventRecording">How much of a job's history the code declares is recorded as events.</param>
/// <param name="EventRecordingEffective">The event recording in force: the override when there is one, else the declared one.</param>
/// <param name="AlertProfile">When, as the code declares it, the engine raises alerts for the definition's jobs on its own.</param>
/// <param name="AlertProfileEffective">The alert profile in force: the override when there is one, else the declared one.</param>
/// <param name="FiringFailurePolicy">What a firing that fails for good does to the definition's schedules: they continue, or scheduling
/// pauses.</param>
/// <param name="ScheduledDelivery">How schedules deliver the definition's work: by firing its one permanent slot job again and again, or
/// by enqueueing a new job for each occurrence.</param>
/// <param name="AlertChannelName">The alert channel the code declares for the definition's automatic alerts.</param>
/// <param name="AlertChannelNameEffective">The alert channel in force: the override when there is one, else the declared one.</param>
/// <param name="RunbookUrl">The runbook link the code declares, shown on the definition's alerts and in the dashboard.</param>
/// <param name="RunbookUrlEffective">The runbook link in force: the override when there is one, else the declared one.</param>
/// <param name="DisplayName">The display name the code declares for the definition.</param>
/// <param name="DisplayNameEffective">The display name in force: the override when there is one, else the declared one.</param>
/// <param name="Description">The description the code declares for the definition.</param>
/// <param name="DescriptionEffective">The description in force: the override when there is one, else the declared one.</param>
/// <param name="Overrides">The operator's overrides, in the shape the overrides update takes: change members of it and send it back
/// whole.</param>
/// <param name="CreatedAtUtc">When the row was created, in UTC.</param>
/// <param name="ModifiedAtUtc">When the row last changed, in UTC.</param>
/// <param name="Version">The definition's version, which every change to it increments; the overrides update and the retire take it
/// as expectedVersion.</param>
public sealed record GetDefinitionResponse(
    [property: JsonIgnore] int DefinitionId,
    string JobNamespace,
    string JobName,
    DefinitionStatusCode Status,
    string DefinitionHash,
    string InputTypeName,
    byte InputFormatId,
    string InputFormatName,
    string? ResultTypeName,
    byte ResultFormatId,
    string ResultFormatName,
    JobPriorityCode Priority,
    JobPriorityCode PriorityEffective,
    int MaxAttempts,
    int MaxAttemptsEffective,
    int? ConcurrencyLimit,
    int? ConcurrencyLimitEffective,
    string? RateLimit,
    string? RateLimitEffective,
    string? RateName,
    string? Lane,
    string Backoff,
    string BackoffEffective,
    int ExecutionTimeoutSeconds,
    int ExecutionTimeoutSecondsEffective,
    int DeadlineSeconds,
    int DeadlineSecondsEffective,
    DeadlineBehaviorCode DeadlineBehavior,
    DeadlineBehaviorCode DeadlineBehaviorEffective,
    int JobRetentionSeconds,
    int JobRetentionSecondsEffective,
    EventRecordingCode EventRecording,
    EventRecordingCode EventRecordingEffective,
    AlertProfileCode AlertProfile,
    AlertProfileCode AlertProfileEffective,
    FiringFailurePolicyCode FiringFailurePolicy,
    ScheduledDeliveryCode ScheduledDelivery,
    string? AlertChannelName,
    string? AlertChannelNameEffective,
    string? RunbookUrl,
    string? RunbookUrlEffective,
    string? DisplayName,
    string? DisplayNameEffective,
    string? Description,
    string? DescriptionEffective,
    DefinitionPolicyOverrides Overrides,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc,
    int Version
);
