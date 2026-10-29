namespace Acta;

/// <summary>The family rows a publication or preview needs: the current version and the one asked for, with their definitions.</summary>
internal sealed record GetCalendarPublicationQuery(string JobNamespace, string CalendarName, int Version);
