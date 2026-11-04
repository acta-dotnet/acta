namespace Acta;

/// <summary>The step slot to read or open under the attempt's fence, and whether it runs at most once.</summary>
internal sealed record StartJobStepCommand(ExecutionFence Fence, string Name, bool AtMostOnce);
