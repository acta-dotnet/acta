namespace Acta;

/// <summary>What retiring a definition did, and the definition's version after the attempt.</summary>
/// <param name="Action">What the call did: applied; version-conflict, when the expected version is stale; or not-found.</param>
/// <param name="Version">The definition's version after the call; on a version conflict, the version it has now; null when the definition
/// was not found.</param>
public sealed record RetireDefinitionResponse(ControlAction Action, int? Version) : IDefinitionControlResponse;
