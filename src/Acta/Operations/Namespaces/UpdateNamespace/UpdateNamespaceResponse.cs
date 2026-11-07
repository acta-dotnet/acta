namespace Acta;

/// <summary>What updating a namespace did, and the namespace's version after the attempt.</summary>
/// <param name="Action">What the call did: applied; version-conflict, when the expected version is stale; or not-found.</param>
/// <param name="Version">The namespace's version after the call; on a version conflict, the version it has now; null when the namespace
/// was not found.</param>
public sealed record UpdateNamespaceResponse(ControlAction Action, int? Version) : IControlResponse;
