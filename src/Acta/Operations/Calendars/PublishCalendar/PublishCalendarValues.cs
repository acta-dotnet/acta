namespace Acta;

/// <summary>
/// One bound schedule as the publication retimes it: the versions the plan read, the binding's new version (one of
/// the two, by the family's role), the new fingerprint and first occurrence, and the audit details of the retime and
/// of the discarded overdue range, when there is one.
/// </summary>
internal sealed record PublishCalendarValues(
    int ScheduleId,
    int ExpectedVersion,
    int ExpectedStateVersion,
    int? CalendarVersion,
    int? AvailabilityCalendarVersion,
    string TimingFingerprint,
    DateTime? NextOccurrenceAtUtc,
    string RetimedDetails,
    string? SkippedDetails
);
