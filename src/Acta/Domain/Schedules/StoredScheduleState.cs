namespace Acta;

/// <summary>
/// Persisted per-schedule cursor and operator lifecycle read at startup, keyed downstream by <c>(DefinitionId, ScheduleName)</c>,
/// driving misfire-aware reconciliation against the current descriptor schedules. <see cref="Status"/> and
/// <see cref="PausedUntilUtc"/> keep a paused schedule out of the recomputed slot cursor across redeploys, or, when timed, only
/// a wake point in it; the <c>Slot</c> fields are the owning slot job's, the same on every row of one slot; the expression
/// kind, expression, time zone, misfire strategy, and description are the stored declaration, and an operator's
/// <see cref="ExpressionOverride"/> matches its kind.
/// </summary>
[DbProjection(ByName = true)]
internal sealed record StoredScheduleState(
    int DefinitionId,
    string ScheduleName,
    DateTime? NextOccurrenceAtUtc,
    ScheduleStatusCode Status,
    DateTime? PausedUntilUtc,
    JobStatusCode? SlotStatus = null,
    DateTime? SlotNextRunAtUtc = null,
    ScheduleExpressionKindCode? ExpressionKind = null,
    string? ExpressionOverride = null,
    string? TimeZoneIdOverride = null,
    string? Expression = null,
    string? TimeZoneId = null,
    MisfireStrategyCode? MisfireStrategy = null,
    string? Description = null,
    long SlotId = 0,
    JobPriorityCode? SlotPriority = null
);
