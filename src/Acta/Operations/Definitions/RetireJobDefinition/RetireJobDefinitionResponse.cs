namespace Acta;

/// <summary>What retiring a definition did.</summary>
public sealed record RetireJobDefinitionResponse(ControlAction Action) : IDefinitionControlResponse;
