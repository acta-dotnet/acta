namespace Acta;

/// <summary>One family's current version: its name, its role, and the Published version.</summary>
internal sealed record ListCurrentCalendarsItem(string CalendarName, CalendarRoleCode Role, int Version);
