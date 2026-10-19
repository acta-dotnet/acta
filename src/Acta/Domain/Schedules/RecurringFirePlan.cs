namespace Acta;

/// <summary>
/// Result of planning one recurring fire: the due schedule names (ordered) the handler sees, the
/// cursor advances to apply on completion, and the post-advance slot MIN (null when the slot is
/// exhausted).
/// </summary>
internal sealed record RecurringFirePlan(
    IReadOnlyList<string> TriggeringScheduleNames,
    IReadOnlyList<ScheduleAdvance> Advances,
    DateTime? SlotMinNextRunAtUtc
)
{
    /// <summary>
    /// The fire only ends timed pauses that leave nothing due: cursors move and the pauses clear, but no
    /// occurrence runs, so the handler is not called. A fire with no advances at all is a trigger-now.
    /// </summary>
    public bool NothingDue => TriggeringScheduleNames.Count == 0 && Advances.Count > 0;
}
