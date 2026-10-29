namespace Acta;

/// <summary>
/// Every schedule bound to the family through the catalog, orphaned ones included: the family's namespace's for a
/// local family, every namespace's for a shared one in <c>sys</c>.
/// </summary>
internal sealed record GetCalendarSchedulesQuery(int NamespaceId, string CalendarName);
