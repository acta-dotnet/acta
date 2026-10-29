using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// One schedule row in a <see cref="ISchedules.ListAsync"/> page. <c>DecidedThroughAtUtc</c> is the watermark every
/// earlier occurrence was decided through, skips included; <c>NextEvaluationAtUtc</c> and <c>EvaluationStatus</c> say
/// when and why the engine next looks; <c>Status</c> is Active, Paused, or Orphaned, with <c>PausedUntilUtc</c> for a
/// timed pause; <c>Version</c> is the row version a CAS control verb takes as expected; <c>JobRef</c> is the recurring
/// slot job's public ref, null when the slot row was purged. The ids stay off the wire: the list pages by
/// <c>ScheduleId</c> inside its opaque cursor.
/// </summary>
public sealed record ListSchedulesItem(
    [property: JsonIgnore] int ScheduleId,
    [property: JsonIgnore] long JobId,
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
    DateTime? DecidedThroughAtUtc,
    DateTime? NextEvaluationAtUtc,
    EvaluationStatusCode EvaluationStatus,
    ScheduleStatusCode Status,
    DateTime? PausedUntilUtc,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc,
    int Version,
    JobRef? JobRef
);
