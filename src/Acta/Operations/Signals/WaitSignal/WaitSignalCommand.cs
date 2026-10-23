namespace Acta;

/// <summary>
/// The slot an attempt waits on under its fence, and the wait's length in database-clock seconds (null is unbounded).
/// </summary>
internal sealed record WaitSignalCommand(ExecutionFence Fence, CheckpointKindCode Kind, string Name, int? TimeoutSeconds);
