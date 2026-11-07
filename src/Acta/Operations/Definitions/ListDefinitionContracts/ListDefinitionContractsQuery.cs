using System.Data.Common;

namespace Acta;

/// <summary>The namespace whose whole catalog to read, inside <paramref name="Transaction"/> when a deploy owns one.</summary>
internal sealed record ListDefinitionContractsQuery(int NamespaceId, DbTransaction? Transaction = null);
