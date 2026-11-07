namespace Acta;

/// <summary>
/// One calendar family in a page. A shared family in the sys namespace counts the schedules of every namespace as its dependents.
/// </summary>
/// <param name="JobNamespace">The namespace that holds the calendar family.</param>
/// <param name="CalendarName">The calendar family's name within its namespace.</param>
/// <param name="Role">The calendar family's role, which says how schedules use it.</param>
/// <param name="CurrentVersion">The number of the published version in force; null when none is published. The publish and the
/// archive take it as expectedCurrentVersion, and a publish sends 0 when none is published.</param>
/// <param name="LatestVersion">The number of the family's newest version; the draft save takes it as expectedLatestVersion.</param>
/// <param name="LatestStatus">The status of the family's newest version.</param>
/// <param name="PublishedAtUtc">When the version in force was published, in UTC; null when none is published.</param>
/// <param name="ModifiedAtUtc">When the row last changed, in UTC.</param>
/// <param name="DependentCount">How many schedules name the family, leaving out orphaned ones and those of a retired definition: the
/// live bindings that refuse an archive, and that report evaluation errors while the family has no current version.</param>
public sealed record ListCalendarsItem(
    string JobNamespace,
    string CalendarName,
    CalendarRoleCode Role,
    int? CurrentVersion,
    int LatestVersion,
    CalendarStatusCode LatestStatus,
    DateTime? PublishedAtUtc,
    DateTime ModifiedAtUtc,
    int DependentCount
);
