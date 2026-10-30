namespace Acta;

/// <summary>
/// The calendar catalog: named calendar families a namespace keeps as data, each a numbered series of immutable versions
/// whose one Published version is current. An operator saves drafts, publishes, and archives. A schedule names a
/// calendar, and the name resolves at every evaluation: a calendar declared in the process first, then the namespace's
/// current family, then the shared one in <c>sys</c>. Reached through <see cref="IActaOperations.Calendars"/>.
/// </summary>
public interface ICalendars
{
    /// <summary>
    /// Lists calendar families by name, one item per family: its current version if it has one, its latest version and
    /// that version's status, and how many live schedules name it in its scope. A query naming a namespace lists
    /// that namespace's families and the shared ones in <c>sys</c>; one naming none lists every namespace's.
    /// </summary>
    ValueTask<ListCalendarsResponse> ListAsync(ListCalendarsQuery query, CancellationToken ct = default);

    /// <summary>
    /// Reads one family: every version newest first, the schedules that name it in its scope (orphaned ones included,
    /// their status says so), and one version's definition JSON: <paramref name="version"/> when given, else the current
    /// version, else the latest. Null when the family has no version, or has no <paramref name="version"/>.
    /// </summary>
    ValueTask<GetCalendarResponse?> GetAsync(CalendarLookup calendar, int? version = null, CancellationToken ct = default);

    /// <summary>
    /// Saves <paramref name="definition"/> as the family's next version, a Draft that no schedule follows until it is published.
    /// <paramref name="expectedLatestVersion"/> is the latest version the caller saw, 0 for a new family; another is
    /// VersionConflict. A <paramref name="role"/> other than the family's, a name the other scope already holds (a shared
    /// calendar in <c>sys</c> against a namespace's, or the reverse), or a definition that does not compile is Rejected
    /// with the problems. A namespace no worker has run in yet is created. Applied answers the new version and its content
    /// hash and records <c>calendar.draft-saved</c>. A role other than Occurrence or Availability throws.
    /// </summary>
    ValueTask<SaveCalendarDraftResponse> SaveDraftAsync(
        CalendarLookup calendar,
        CalendarRoleCode role,
        string definition,
        int expectedLatestVersion,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Forecasts one version beside the current one over the same horizon: the next <paramref name="count"/> instants
    /// (occurrence) or windows (availability) from <paramref name="fromUtc"/>, the database clock when null, in
    /// <paramref name="timeZoneId"/> (UTC when null), and the bound schedules with whether each holds an overdue occurrence.
    /// Null when the family has no such version. No cursor is read or written.
    /// </summary>
    ValueTask<PreviewCalendarResponse?> PreviewAsync(
        CalendarLookup calendar,
        int version,
        DateTime? fromUtc = null,
        int count = 10,
        string? timeZoneId = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Makes <paramref name="candidateVersion"/>, a Draft, the family's current version and retimes every schedule that
    /// names the family in its scope in one transaction: its cursor seeds from the candidate's first occurrence after now,
    /// and <c>schedule.retimed</c> is recorded. <paramref name="expectedCurrentVersion"/> is the current version the caller
    /// saw, 0 for none; another is VersionConflict. A bound schedule holding an undecided overdue occurrence is Rejected
    /// under <see cref="OverdueDecisionCode.Refuse"/> and skipped as an audited range under
    /// <see cref="OverdueDecisionCode.Discard"/>. NotFound when the version is not the family's; Applied records <c>calendar.published</c>.
    /// </summary>
    ValueTask<PublishCalendarResponse> PublishAsync(
        CalendarLookup calendar,
        int expectedCurrentVersion,
        int candidateVersion,
        OverdueDecisionCode overdue = OverdueDecisionCode.Refuse,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Archives the family's current version, after which the family has no current version until another is published.
    /// NotFound when the family has no current version; Rejected when the current version is not
    /// <paramref name="expectedCurrentVersion"/>, or while a live schedule of an active definition names the family in its
    /// scope, answering how many do. Applied records <c>calendar.archived</c> with <paramref name="reasonMessage"/> and
    /// <paramref name="actor"/>.
    /// </summary>
    ValueTask<ArchiveCalendarResponse> ArchiveAsync(
        CalendarLookup calendar,
        int expectedCurrentVersion,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    );
}
