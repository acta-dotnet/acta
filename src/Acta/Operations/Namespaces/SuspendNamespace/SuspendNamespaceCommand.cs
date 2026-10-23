namespace Acta;

/// <summary>One namespace suspension as the facade received it.</summary>
internal sealed record SuspendNamespaceCommand(string NamespaceName, string? ReasonMessage, string? Actor);
