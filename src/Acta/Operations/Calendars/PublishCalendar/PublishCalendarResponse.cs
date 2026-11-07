namespace Acta;

/// <summary>
/// The outcome of a publication: Applied with the version now current and the schedules retimed (as
/// <c>job/schedule</c> paths), NotFound when the candidate is no version of the family, VersionConflict when the
/// current version is not the one the caller saw, or Rejected with the reason and, when the old version left overdue
/// occurrences undecided, the schedules holding them.
/// </summary>
/// <param name="Action">What the call did, such as applied, already-in-state, rejected, not-found, or version-conflict.</param>
/// <param name="Version">The number of the version now in force; null when nothing was published.</param>
/// <param name="RetimedSchedules">The schedules the publication retimed.</param>
/// <param name="OverdueSchedules">The schedules that hold an overdue occurrence.</param>
/// <param name="Reason">Why the publication was refused, when it was.</param>
public sealed record PublishCalendarResponse(
    ControlAction Action,
    int? Version,
    IReadOnlyList<string> RetimedSchedules,
    IReadOnlyList<string> OverdueSchedules,
    string? Reason
) : ICalendarControlResponse;
