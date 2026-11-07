using System.Data.Common;

namespace Acta;

/// <summary>The namespace whose stored schedule cursors to read, inside <paramref name="Transaction"/> when a deploy owns one.</summary>
internal sealed record ListScheduleStatesQuery(int NamespaceId, DbTransaction? Transaction = null);
