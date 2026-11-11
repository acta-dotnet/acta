namespace Acta;

/// <summary>What suspending a namespace did, and the namespace's version after the attempt.</summary>
/// <param name="Action">What the call did: applied; already-in-state, when it was already as asked; or not-found.</param>
/// <param name="Version">The namespace's version after the call; null when the namespace was not found.</param>
public sealed record SuspendNamespaceResponse(ControlAction Action, long? Version) : IControlResponse;
