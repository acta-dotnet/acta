namespace Acta;

/// <summary>
/// The namespace and worker ids the bootstrap routine assigns, and the catalog hash the namespace's last registration
/// stored - the single-row result of the start.
/// </summary>
[DbProjection(ByName = true)]
internal sealed record StartWorkerResponse(int NamespaceId, int WorkerId, string? CatalogHash);
