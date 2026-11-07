namespace Acta;

/// <summary>
/// What registration reads of a stored schedule at a deploy, keyed downstream by <c>(DefinitionId, ScheduleName)</c>:
/// the cursor, the timing fingerprint and interval anchor it was registered under, and the owning slot's status, the
/// same on every row of one slot and null for a PerOccurrence definition, which has no slot.
/// </summary>
[DbProjection(ByName = true)]
internal sealed record StoredScheduleState(
    int DefinitionId,
    string ScheduleName,
    DateTime? NextOccurrenceAtUtc,
    JobStatusCode? SlotStatus = null,
    ScheduleExpressionKindCode? ExpressionKind = null,
    DateTime? IntervalAnchorAtUtc = null,
    string? TimingFingerprint = null
);
