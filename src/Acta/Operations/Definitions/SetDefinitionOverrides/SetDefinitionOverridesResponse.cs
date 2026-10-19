namespace Acta;

/// <summary>What setting a definition's policy overrides did, and the definition's version after the attempt.</summary>
public sealed record SetDefinitionOverridesResponse(ControlAction Action, int? Version) : IDefinitionControlResponse;
