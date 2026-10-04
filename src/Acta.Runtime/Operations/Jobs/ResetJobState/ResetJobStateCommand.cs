namespace Acta.Runtime;

/// <summary>The job whose durable step and checkpoint state to clear.</summary>
internal sealed record ResetJobStateCommand(long JobId);
