namespace Acta;

/// <summary>What suspending a namespace did, and the namespace's version after the attempt.</summary>
/// <param name="Action">What the call did, such as applied, already-in-state, rejected, not-found, or version-conflict.</param>
/// <param name="Version">The namespace's version after the call; on a version conflict, the version it has now; null when the namespace
/// was not found.</param>
public sealed record SuspendNamespaceResponse(ControlAction Action, int? Version) : IControlResponse;
