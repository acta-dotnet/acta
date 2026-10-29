namespace Acta;

/// <summary>The family to read, and the version whose definition to show; null shows the current one, else the latest.</summary>
internal sealed record GetCalendarQuery(CalendarLookup Calendar, int? Version);
