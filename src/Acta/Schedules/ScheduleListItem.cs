namespace Acta;

/// <summary>
/// One schedule row in a <see cref="ISchedules.ListAsync"/> page. Expression and
/// time zone are the effective values: the operator override when present, else the original.
/// </summary>
/// <param name="JobScheduleId">Schedule row id.</param> <param name="JobId">Recurring slot job id.</param> <param name="JobDefinitionId">Catalog definition id.</param>
/// <param name="JobNamespace">Owning namespace name.</param> <param name="JobName">Job definition name.</param> <param name="ScheduleName">Schedule name within the job.</param>
/// <param name="Origin">How the schedule was registered.</param> <param name="ExpressionKind">Expression syntax kind.</param>
/// <param name="Expression">Effective schedule expression.</param> <param name="TimeZoneId">Effective IANA time zone id.</param> <param name="MisfireStrategy">MisfireStrategy handling.</param>
/// <param name="NextRunAtUtc">Next computed fire instant, or null.</param> <param name="LastRunAtUtc">Most recent occurrence this schedule was advanced past (including misfire skips), or null before the first advance.</param>
/// <param name="Status">Lifecycle state (Active / Paused / Orphaned).</param> <param name="PausedUntilUtc">When a timed pause expires, or null.</param>
/// <param name="CreatedAtUtc">Row insert instant.</param> <param name="ModifiedAtUtc">Last row change instant.</param>
/// <param name="Version">Optimistic-concurrency row version; pass as the expected version to a CAS control verb.</param>
public sealed record ScheduleListItem(
    long JobScheduleId,
    long JobId,
    int JobDefinitionId,
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
    int Version
);
