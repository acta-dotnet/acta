namespace Acta;

/// <summary>What updating a namespace did, and the namespace's version after the attempt.</summary>
public sealed record UpdateNamespaceResponse(ControlAction Action, int? Version) : IControlResponse;
