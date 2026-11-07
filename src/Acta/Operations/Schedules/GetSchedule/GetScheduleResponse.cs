using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// One schedule: what it fires and when, how it behaves when occurrences pile up or go stale, and where the engine stands with it. A
/// schedule is addressed by its namespace, job name, and schedule name.
/// </summary>
/// <param name="ScheduleId">The schedule's internal id; the JSON omits it.</param>
/// <param name="JobId">The internal id of the permanent slot job the schedule fires; the JSON omits it and carries jobRef.</param>
/// <param name="DefinitionId">The internal id of the schedule's definition; the JSON omits it.</param>
/// <param name="JobNamespace">The namespace of the schedule's definition.</param>
/// <param name="JobName">The name of the schedule's definition.</param>
/// <param name="ScheduleName">The schedule's name within its definition.</param>
/// <param name="Origin">Whether the schedule is declared by the definition's code or was added by an operator.</param>
/// <param name="ExpressionKind">Whether expression is a cron expression, an interval, or a calendar's name.</param>
/// <param name="Expression">The cron expression, the interval such as 5m or PT5M, or the calendar's name.</param>
/// <param name="TimeZoneId">The IANA time zone the schedule's expression is evaluated in.</param>
/// <param name="MisfireStrategy">What the schedule does about occurrences it missed during downtime.</param>
/// <param name="NextOccurrenceAtUtc">The schedule's next occurrence, in UTC; null when it has none.</param>
/// <param name="NextEvaluationAtUtc">When the engine next looks at the schedule, in UTC; null while nothing timed ends its wait.</param>
/// <param name="EvaluationStatus">Why the next evaluation is when it is.</param>
/// <param name="Status">The schedule's status.</param>
/// <param name="PausedUntilUtc">When the schedule's pause ends, in UTC; null when it is not paused or is paused until resumed.</param>
/// <param name="CreatedAtUtc">When the row was created, in UTC.</param>
/// <param name="ModifiedAtUtc">When the row last changed, in UTC.</param>
/// <param name="Version">The schedule's version, which every change to it increments.</param>
/// <param name="JobRef">The ref of the permanent slot job the schedule fires; null for a schedule that enqueues a new job for each
/// occurrence. A permanent schedule's events are its slot job's, read at GET /jobs/{jobRef}/events with this ref;
/// GET /definitions/{jobNamespace}/{jobName}/events lists every schedule's events, a per-occurrence schedule's included. Each names its
/// schedule in detailsText.</param>
/// <param name="MaxOutstanding">How many admitted occurrences may be unsettled at once.</param>
/// <param name="OutstandingCount">How many admitted occurrences are unsettled now.</param>
/// <param name="FullBehavior">What a due occurrence does while the schedule is at maxOutstanding: wait or drop.</param>
/// <param name="ScheduledDelivery">How the schedule delivers work: by firing the definition's permanent slot job, or by enqueueing a new
/// job for each occurrence.</param>
/// <param name="ExpireAfterSeconds">How old an occurrence may get before it expires unadmitted, in seconds; null means it never
/// expires.</param>
/// <param name="ExpireAction">What an expired occurrence does: it is discarded, or the schedule pauses. Null when occurrences never
/// expire.</param>
/// <param name="AvailabilityCalendarName">The availability calendar the schedule observes; null when it observes none.</param>
/// <param name="StartAtUtc">The first instant an occurrence may fall on, in UTC; null for no lower bound.</param>
/// <param name="EndBeforeUtc">The instant from which no occurrence falls, in UTC; null for no upper bound. The bound is on the
/// occurrence's own time, not on when it runs.</param>
public sealed record GetScheduleResponse(
    [property: JsonIgnore] int ScheduleId,
    [property: JsonIgnore] long? JobId,
    [property: JsonIgnore] int DefinitionId,
    string JobNamespace,
    string JobName,
    string ScheduleName,
    ScheduleOriginCode Origin,
    ScheduleExpressionKindCode ExpressionKind,
    string Expression,
    string TimeZoneId,
    MisfireStrategyCode MisfireStrategy,
    DateTime? NextOccurrenceAtUtc,
    DateTime? NextEvaluationAtUtc,
    ScheduleEvaluationStatusCode EvaluationStatus,
    ScheduleStatusCode Status,
    DateTime? PausedUntilUtc,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc,
    int Version,
    JobRef? JobRef,
    int MaxOutstanding,
    int OutstandingCount,
    ScheduleFullBehaviorCode FullBehavior,
    ScheduledDeliveryCode ScheduledDelivery,
    int? ExpireAfterSeconds,
    ScheduleExpireActionCode? ExpireAction,
    string? AvailabilityCalendarName,
    DateTime? StartAtUtc,
    DateTime? EndBeforeUtc
);
