using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// One schedule row in a <see cref="ISchedules.ListAsync"/> page. <c>Expression</c> and <c>TimeZoneId</c> are the
/// effective values, the operator override when present and else the original; <c>LastRunAtUtc</c> is the most recent
/// occurrence the schedule was advanced past, misfires included; <c>Status</c> is Active, Paused, or Orphaned, with
/// <c>PausedUntilUtc</c> for a timed pause; <c>Version</c> is the row version a CAS control verb takes as expected;
/// <c>JobRef</c> is the recurring slot job's public ref, null when the slot row was purged.
/// </summary>
public sealed record ListSchedulesItem(
    [property: JsonIgnore] long JobScheduleId,
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
    DateTime? NextRunAtUtc,
    DateTime? LastRunAtUtc,
    ScheduleStatusCode Status,
    DateTime? PausedUntilUtc,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc,
    int Version,
    JobRef? JobRef
);
