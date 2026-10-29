namespace Acta;

/// <summary>
/// One schedule as the admitter plans it: its declaration, timing, calendar bindings, capacity, and expiry, its state with
/// the version the plan will carry, its definition's delivery, and its slot's status, version, and envelope status, the
/// same on every member of one slot and null for a schedule with no slot. Read without locks; the apply validates the
/// versions.
/// </summary>
[DbProjection(ByName = true)]
internal sealed record DueSchedule(
    int ScheduleId,
    int NamespaceId,
    string NamespaceName,
    long? JobId,
    int DefinitionId,
    string ScheduleName,
    string DefinitionName,
    ScheduledDeliveryCode ScheduledDelivery,
    string Expression,
    string? TimeZoneId,
    ScheduleExpressionKindCode ExpressionKind,
    MisfireStrategyCode MisfireStrategy,
    int MisfireGraceSeconds,
    DateTime? IntervalAnchorAtUtc,
    string TimingFingerprint,
    CalendarOriginCode? CalendarOrigin,
    int? CalendarVersion,
    string? AvailabilityCalendarName,
    CalendarOriginCode? AvailabilityOrigin,
    int? AvailabilityVersion,
    int MaxOutstanding,
    ScheduleFullBehaviorCode FullBehavior,
    int? ExpireAfterSeconds,
    ScheduleExpireActionCode? ExpireAction,
    ScheduleStatusCode Status,
    DateTime? PausedUntilUtc,
    int ScheduleVersion,
    DateTime? NextOccurrenceAtUtc,
    DateTime? NextEvaluationAtUtc,
    EvaluationStatusCode EvaluationStatus,
    int OutstandingCount,
    int StateVersion,
    JobStatusCode? SlotStatus,
    int? SlotVersion,
    CheckpointStatusCode? FiringStatus
)
{
    /// <summary>The schedule's timing with its bindings as registration resolved them.</summary>
    public ScheduleTiming Timing() =>
        new(
            Expression,
            ExpressionKind,
            TimeZoneId,
            IntervalAnchorAtUtc,
            CalendarOrigin,
            CalendarVersion,
            AvailabilityCalendarName,
            AvailabilityOrigin,
            AvailabilityVersion
        );
}
