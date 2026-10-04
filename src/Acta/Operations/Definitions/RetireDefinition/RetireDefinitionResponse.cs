namespace Acta;

/// <summary>What retiring a definition did.</summary>
public sealed record RetireDefinitionResponse(ControlAction Action) : IDefinitionControlResponse;
