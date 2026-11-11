namespace Acta;

/// <summary>
/// One schedule as the admitter plans it: its declaration, timing, calendar names, capacity, and expiry, its state with
/// the version the plan will carry, and its slot's status, version, and envelope status, the same on every member of
/// one slot and null for a schedule with no slot. Read without locks; the apply validates the versions.
/// </summary>
[DbProjection(ByName = true)]
internal sealed record DueSchedule(
    int ScheduleId,
    int NamespaceId,
    string NamespaceName,
    long? JobId,
    string ScheduleName,
    string DefinitionName,
    string Expression,
    string? TimeZoneId,
    ScheduleExpressionKindCode ExpressionKind,
    MisfireStrategyCode MisfireStrategy,
    DateTime? IntervalAnchorAtUtc,
    string? AvailabilityCalendarName,
    DateTime? StartAtUtc,
    DateTime? EndBeforeUtc,
    int MaxOutstanding,
    ScheduleFullBehaviorCode FullBehavior,
    int? ExpireAfterSeconds,
    ScheduleExpireActionCode? ExpireAction,
    ScheduleStatusCode Status,
    DateTime? PausedUntilUtc,
    int ScheduleVersion,
    DateTime? NextOccurrenceAtUtc,
    DateTime? NextEvaluationAtUtc,
    ScheduleEvaluationStatusCode EvaluationStatus,
    int OutstandingCount,
    int StateVersion,
    JobStatusCode? SlotStatus,
    long? SlotVersion,
    CheckpointStatusCode? FiringStatus
)
{
    /// <summary>
    /// Whether this build knows every code the schedule holds. A newer build can store one this build does not have
    /// while both serve the namespace; the admitter's reads leave such a schedule out, and a publication leaves it be.
    /// </summary>
    public bool Readable =>
        Known(Status)
        && Known(ExpressionKind)
        && Known(MisfireStrategy)
        && Known(FullBehavior)
        && (ExpireAction is not { } action || Known(action));

    private static bool Known<T>(T code)
        where T : struct, Enum => Enum.IsDefined(code) && !code.Equals(default(T));

    /// <summary>The schedule's timing as stored.</summary>
    public ScheduleTiming Timing() =>
        new(Expression, ExpressionKind, TimeZoneId, IntervalAnchorAtUtc, AvailabilityCalendarName, StartAtUtc, EndBeforeUtc);
}
