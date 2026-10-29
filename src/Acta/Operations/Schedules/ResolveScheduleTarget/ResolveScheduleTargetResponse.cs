namespace Acta;

/// <summary>
/// The schedule a control verb addresses: its identity, its slot job (null for a PerOccurrence definition, which has
/// none), its definition and namespace, the database clock the verb decides against, the schedule as stored, and the
/// capacity a per-occurrence trigger charges against at the state version it was read.
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
    int StateVersion
);
