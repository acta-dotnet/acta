using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// One schedule row in a <see cref="ISchedules.ListAsync"/> page. <c>NextOccurrenceAtUtc</c> is the next undecided occurrence;
/// <c>NextEvaluationAtUtc</c> and <c>EvaluationStatus</c> say when and why the engine next looks; <c>Status</c> is Active, Paused,
/// or Orphaned, with <c>PausedUntilUtc</c> for a timed pause; <c>Version</c> is the row version a CAS control verb takes as
/// expected; <c>JobRef</c> is the permanent slot's public ref, null for a PerOccurrence definition; <c>OutstandingCount</c> counts
/// its unsettled roots; <c>ExpireAfterSeconds</c> and <c>ExpireAction</c> are null together when occurrences never expire. The
/// ids stay off the wire: the list pages by <c>ScheduleId</c> inside its opaque cursor.
/// </summary>
public sealed record ListSchedulesItem(
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
    EvaluationStatusCode EvaluationStatus,
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
    ScheduleExpireActionCode? ExpireAction
);
