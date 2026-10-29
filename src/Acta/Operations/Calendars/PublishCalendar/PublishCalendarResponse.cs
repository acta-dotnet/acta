namespace Acta;

/// <summary>
/// The outcome of a publication: Applied with the version now current and the schedules retimed (as
/// <c>job/schedule</c> paths), NotFound when the candidate is no version of the family, VersionConflict when the
/// current version is not the one the caller saw, or Rejected with the reason and, when the old version left overdue
/// occurrences undecided, the schedules holding them.
/// </summary>
public sealed record PublishCalendarResponse(
    ControlAction Action,
    int? Version,
    IReadOnlyList<string> RetimedSchedules,
    IReadOnlyList<string> OverdueSchedules,
    string? Reason
) : ICalendarControlResponse;
