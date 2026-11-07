using System.Collections.Immutable;
using System.Data.Common;

namespace Acta;

/// <summary>
/// A namespace's whole definitions set, with the catalog as the caller read it; written through
/// <paramref name="Transaction"/> when a deploy owns one.
/// </summary>
internal sealed record RegisterDefinitionsCommand(
    int NamespaceId,
    ImmutableArray<JobDescriptor> Descriptors,
    IReadOnlyList<StoredDefinitionContract> Stored,
    DbTransaction? Transaction = null
);
