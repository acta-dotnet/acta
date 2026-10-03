namespace Acta;

/// <summary>What setting a definition's policy overrides did.</summary>
public sealed record SetJobDefinitionOverridesResponse(ControlAction Action) : IDefinitionControlResponse;
