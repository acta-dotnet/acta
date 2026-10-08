namespace Acta.Runtime;

/// <summary>
/// The namespace and worker ids the bootstrap routine assigns - the single-row result of the start.
/// </summary>
[DbProjection]
internal sealed record StartWorkerResponse(int NamespaceId, int WorkerId);
