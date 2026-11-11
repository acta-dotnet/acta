namespace Acta;

/// <summary>What setting a definition's policy overrides did, and the definition's version after the attempt.</summary>
/// <param name="Action">What the call did: applied; version-conflict, when the expected version is stale; or not-found.</param>
/// <param name="Version">The definition's version after the call; on a version conflict, the version it has now; null when the definition
/// was not found.</param>
public sealed record UpdateDefinitionOverridesResponse(ControlAction Action, long? Version) : IDefinitionControlResponse;
