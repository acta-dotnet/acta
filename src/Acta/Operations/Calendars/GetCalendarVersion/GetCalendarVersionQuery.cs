namespace Acta;

/// <summary>The version of one family to read, by the namespace's id.</summary>
internal sealed record GetCalendarVersionQuery(int NamespaceId, string CalendarName, int Version);
