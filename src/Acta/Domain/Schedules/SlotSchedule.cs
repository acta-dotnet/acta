namespace Acta;

/// <summary>
/// One declared schedule's reconciled state for a definition's slot upsert. <c>NextOccurrenceAtUtc</c> is the
/// computed cursor; null means the expression has no upcoming occurrence (exhausted).
/// </summary>
internal sealed record SlotSchedule(
    string ScheduleName,
    string Expression,
    string? TimeZoneId,
    MisfireStrategyCode MisfireStrategy,
    ScheduleExpressionKindCode ExpressionKind,
    string? Description,
    DateTime? NextOccurrenceAtUtc
);
