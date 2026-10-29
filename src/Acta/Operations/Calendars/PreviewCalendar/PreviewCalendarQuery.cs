namespace Acta;

/// <summary>
/// <see cref="ICalendars.PreviewAsync"/>: the next instants or windows of one version, from <see cref="FromUtc"/>
/// (the database clock when null) in a zone (UTC when null), beside the current version's, and the schedules the
/// family binds with whether each holds an overdue occurrence.
/// </summary>
internal sealed record PreviewCalendarQuery(CalendarLookup Calendar, int Version, DateTime? FromUtc, int Count, string? TimeZoneId);
