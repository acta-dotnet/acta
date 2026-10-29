namespace Acta;

/// <summary>
/// One schedule as the admitter plans it: its declaration and timing, its state with the version the plan will
/// carry, and its slot's status, version, and envelope status, the same on every member of one slot. Read without
/// locks; the apply validates the versions.
/// </summary>
[DbProjection(ByName = true)]
internal sealed record GetDueSchedulesRow(
    int ScheduleId,
    long JobId,
    string ScheduleName,
    string DefinitionName,
    string Expression,
    string? TimeZoneId,
    ScheduleExpressionKindCode ExpressionKind,
    MisfireStrategyCode MisfireStrategy,
    int MisfireGraceSeconds,
    DateTime? IntervalAnchorAtUtc,
    string TimingFingerprint,
    ScheduleStatusCode Status,
    DateTime? PausedUntilUtc,
    DateTime? NextOccurrenceAtUtc,
    DateTime? DecidedThroughAtUtc,
    DateTime? NextEvaluationAtUtc,
    EvaluationStatusCode EvaluationStatus,
    int StateVersion,
    JobStatusCode SlotStatus,
    int SlotVersion,
    CheckpointStatusCode? FiringStatus
);
