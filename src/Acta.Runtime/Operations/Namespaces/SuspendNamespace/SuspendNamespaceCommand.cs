namespace Acta.Runtime;

/// <summary>One namespace suspension as the facade received it.</summary>
internal sealed record SuspendNamespaceCommand(string Name, string? ReasonMessage, string? ActorKey);
