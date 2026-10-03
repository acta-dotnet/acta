using System.Collections.Immutable;

namespace Acta.Runtime;

/// <summary>A namespace's whole definitions set, with the catalog as startup read it.</summary>
internal sealed record RegisterJobDefinitionsCommand(
    int NamespaceId,
    DateTime ManifestGenerationUtc,
    ImmutableArray<JobDescriptor> Descriptors,
    IReadOnlyList<StoredDefinitionContract> Stored
);
