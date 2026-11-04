namespace Acta;

/// <summary>The family's head as GetCalendar reads it: its namespace id, its Published version if any, and its latest version.</summary>
[DbProjection]
internal sealed record GetCalendarRow(int NamespaceId, int? CurrentVersion, int LatestVersion);
