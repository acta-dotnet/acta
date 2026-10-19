namespace Acta;

/// <summary>
/// One catalog contribution to typed-enqueue routing: a namespace and the manifests whose descriptors
/// are visible there. Produced by <c>IActaBuilder.Reference</c> and by every worker registration.
/// </summary>
internal sealed record CatalogDeclaration(string NamespaceName, IReadOnlyList<ManifestRegistration> Manifests);
