using System.Data.Common;

namespace Acta;

/// <summary>
/// The version of one family to read, by the namespace's id, inside <paramref name="Transaction"/> when a deploy owns
/// one.
/// </summary>
internal sealed record GetCalendarVersionQuery(int NamespaceId, string CalendarName, int Version, DbTransaction? Transaction = null);
