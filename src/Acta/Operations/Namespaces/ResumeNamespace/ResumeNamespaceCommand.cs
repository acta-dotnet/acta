namespace Acta;

/// <summary>One namespace resumption as the facade received it.</summary>
internal sealed record ResumeNamespaceCommand(string NamespaceName, string? ReasonMessage, string? Actor);
