namespace Acta;

/// <summary>What resuming a namespace did, and the namespace's version after the attempt.</summary>
public sealed record ResumeNamespaceResponse(ControlAction Action, int? Version) : IControlResponse;
