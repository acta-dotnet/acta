namespace Acta;

/// <summary>What retiring a definition did, and the definition's version after the attempt.</summary>
public sealed record RetireDefinitionResponse(ControlAction Action, int? Version) : IDefinitionControlResponse;
