namespace Acta.Runtime;

/// <summary>The alert an operator resolves, with their note and key for the audit event.</summary>
internal sealed record ResolveJobAlertManualCommand(AlertRef AlertRef, string? ReasonMessage, string? ActorKey);
