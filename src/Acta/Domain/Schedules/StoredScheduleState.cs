namespace Acta;

/// <summary>
/// What registration reads of a stored schedule at worker start, keyed downstream by <c>(DefinitionId, ScheduleName)</c>:
/// the cursor, the timing fingerprint and interval anchor it was registered under, the capacity, expiry, and
/// availability calendar of the stored declaration, and the owning slot's status and priority, the same on every row of
/// one slot and null for a PerOccurrence definition, which has no slot.
/// </summary>
[DbProjection(ByName = true)]
internal sealed record StoredScheduleState(
    int DefinitionId,
    string ScheduleName,
    DateTime? NextOccurrenceAtUtc,
    JobStatusCode? SlotStatus = null,
    ScheduleExpressionKindCode? ExpressionKind = null,
    JobPriorityCode? SlotPriority = null,
    DateTime? IntervalAnchorAtUtc = null,
    string? TimingFingerprint = null,
    int? MaxOutstanding = null,
    ScheduleFullBehaviorCode? FullBehavior = null,
    int? ExpireAfterSeconds = null,
    ScheduleExpireActionCode? ExpireAction = null,
    string? AvailabilityCalendarName = null
);
