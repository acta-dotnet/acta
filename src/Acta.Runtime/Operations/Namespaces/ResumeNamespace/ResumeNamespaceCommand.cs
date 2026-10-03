namespace Acta.Runtime;

/// <summary>One namespace resumption as the facade received it.</summary>
internal sealed record ResumeNamespaceCommand(string Name, string? ReasonMessage, string? ActorKey);
