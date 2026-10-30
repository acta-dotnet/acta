namespace Acta;

/// <summary>
/// The live schedule a path names, with its identity, its namespace's status, its slot, null when it has none, its
/// timing with the calendar names it binds, and the capacity a per-occurrence trigger charges against, at the state
/// version it was read.
/// </summary>
[DbProjection(ByName = true)]
internal sealed record ResolveScheduleTargetRow(
    int ScheduleId,
    long? JobId,
    int NamespaceId,
    NamespaceStatusCode NamespaceStatus,
    string ScheduleName,
    string Expression,
    string? TimeZoneId,
    ScheduleExpressionKindCode ExpressionKind,
    DateTime? NextOccurrenceAtUtc,
    ScheduleStatusCode Status,
    DateTime? PausedUntilUtc,
    int Version,
    int MaxOutstanding,
    int OutstandingCount,
    int StateVersion,
    DateTime? IntervalAnchorAtUtc,
    string? AvailabilityCalendarName
);
