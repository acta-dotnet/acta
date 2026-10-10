namespace Acta;

/// <summary>The alert an operator resolves, with their note and key for the audit event.</summary>
internal sealed record ResolveAlertCommand(AlertRef AlertRef, string? ReasonMessage, string? Actor);
