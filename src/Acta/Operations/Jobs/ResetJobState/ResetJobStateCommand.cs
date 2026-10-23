namespace Acta;

/// <summary>The attempt whose job's durable step and checkpoint state to clear, under that attempt's fence.</summary>
internal sealed record ResetJobStateCommand(ExecutionFence Fence);
