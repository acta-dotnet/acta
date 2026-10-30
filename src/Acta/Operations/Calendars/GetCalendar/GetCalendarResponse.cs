namespace Acta;

/// <summary>
/// One catalog calendar family as <see cref="ICalendars.GetAsync"/> reads it. <c>CurrentVersion</c> is the Published
/// version, null while the family has none; <c>Versions</c> lists every version newest first; <c>Definition</c> is the
/// JSON of <c>ShownVersion</c>; <c>Dependents</c> lists every schedule that names the family in its scope, by job name and
/// then schedule name.
/// </summary>
public sealed record GetCalendarResponse(
    string JobNamespace,
    string CalendarName,
    CalendarRoleCode Role,
    int? CurrentVersion,
    IReadOnlyList<GetCalendarVersion> Versions,
    int ShownVersion,
    string Definition,
    IReadOnlyList<GetCalendarDependent> Dependents
);
