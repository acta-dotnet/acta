namespace Acta;

/// <summary>
/// What a worker start found and wrote: the namespace's id and current deployed version (null when no deploy has been
/// recorded there), and the worker id it was given, null when its version may not run in the namespace.
/// </summary>
[DbProjection(ByName = true)]
internal sealed record StartWorkerResponse(int NamespaceId, int? WorkerId, string? CurrentDeploymentVersion);
