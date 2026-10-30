namespace Acta;

/// <summary>
/// One bound schedule as the publication retimes it: the versions the plan read, the first occurrence the candidate
/// gives, and the audit details of the retime and of the discarded overdue range, when there is one.
/// </summary>
internal sealed record PublishCalendarValues(
    int ScheduleId,
    int ExpectedVersion,
    int ExpectedStateVersion,
    DateTime? NextOccurrenceAtUtc,
    string RetimedDetails,
    string? SkippedDetails
);
