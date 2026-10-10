namespace Acta;

/// <summary>The alert an operator acknowledges, with their note and key for the audit event.</summary>
internal sealed record AcknowledgeAlertCommand(AlertRef AlertRef, string? ReasonMessage, string? Actor);
