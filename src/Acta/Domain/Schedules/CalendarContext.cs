namespace Acta;

/// <summary>
/// What a calendar is asked: the UTC instant its next occurrence must follow, and the schedule's zone for kinds whose
/// rules are local (a business day, a local time of day). The engine passes the zone; a calendar never reads one.
/// </summary>
internal readonly record struct CalendarContext(DateTime AfterUtc, TimeZoneInfo TimeZone);
