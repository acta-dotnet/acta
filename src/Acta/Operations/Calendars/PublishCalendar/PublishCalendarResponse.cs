namespace Acta;

/// <summary>
/// The outcome of a publication: applied, with the version made current and the schedules retimed, each as namespace/job/schedule;
/// not-found, when the candidate is no version of the family; version-conflict, when the current version is not the one the caller saw;
/// or rejected, with the reason and, when the old version left overdue occurrences undecided, the schedules holding them.
/// </summary>
/// <param name="Action">What the call did: applied, rejected, version-conflict, or not-found.</param>
/// <param name="Version">The number of the version in force after the call: the candidate when applied. Otherwise the family's
/// current version, or null: when none is published, and when the call ended before reading it, as for a candidate that does not
/// exist, is not a draft, or is bound to more schedules than one publication retimes.</param>
/// <param name="RetimedSchedules">The schedules the publication retimed, each as namespace/job/schedule.</param>
/// <param name="OverdueSchedules">The schedules that hold an overdue occurrence, each as namespace/job/schedule.</param>
/// <param name="Reason">Why the publication was refused, when it was.</param>
public sealed record PublishCalendarResponse(
    ControlAction Action,
    int? Version,
    IReadOnlyList<string> RetimedSchedules,
    IReadOnlyList<string> OverdueSchedules,
    string? Reason
) : ICalendarControlResponse;
