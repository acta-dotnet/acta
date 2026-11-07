using System.Data.Common;

namespace Acta;

/// <summary>The namespace whose current calendars to read, inside <paramref name="Transaction"/> when a deploy owns one.</summary>
internal sealed record ListCurrentCalendarsQuery(int NamespaceId, DbTransaction? Transaction = null);
