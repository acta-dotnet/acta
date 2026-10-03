namespace Acta;

/// <summary>What suspending a namespace did, and the namespace's version after the attempt.</summary>
public sealed record SuspendNamespaceResponse(ControlAction Action, int? Version) : IControlResponse;
