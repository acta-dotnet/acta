namespace Acta;

/// <summary>
/// The schedule a control verb addresses: its ids, its slot (null for a per-occurrence definition), its namespace's
/// status, the database clock the verb decides at, its lifecycle and cursor at the row version read, its timing with
/// the calendars it binds, and the capacity a trigger charges against at the state version read.
/// </summary>
internal sealed record ResolveScheduleTargetResponse(
    int ScheduleId,
    long? JobId,
    int NamespaceId,
    NamespaceStatusCode NamespaceStatus,
    DateTime NowUtc,
    string ScheduleName,
    ScheduleStatusCode Status,
    DateTime? PausedUntilUtc,
    DateTime? NextOccurrenceAtUtc,
    int Version,
    int MaxOutstanding,
    int OutstandingCount,
    int StateVersion,
    ScheduleTiming Timing
);
