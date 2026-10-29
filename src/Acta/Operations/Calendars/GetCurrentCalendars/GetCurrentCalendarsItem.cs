namespace Acta;

/// <summary>One family's current version: its name, its role, and the Published version.</summary>
internal sealed record GetCurrentCalendarsItem(string CalendarName, CalendarRoleCode Role, int Version);
