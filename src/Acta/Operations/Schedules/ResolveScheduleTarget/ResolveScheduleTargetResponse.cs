namespace Acta;

/// <summary>
/// The schedule a control verb addresses: its ids, its slot (null for a per-occurrence definition), the database clock
/// the verb decides at, the live schedule, its timing with the calendars it binds, and the capacity a trigger charges
/// against at the state version read.
/// </summary>
internal sealed record ResolveScheduleTargetResponse(
    int ScheduleId,
    long? JobId,
    int DefinitionId,
    int NamespaceId,
    DateTime NowUtc,
    LiveSchedule Schedule,
    int MaxOutstanding,
    int OutstandingCount,
    int StateVersion,
    ScheduleTiming Timing
);
