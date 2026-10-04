namespace Acta.Runtime;

/// <summary>The slot a job waits on, and the wait's length in database-clock seconds (null is unbounded).</summary>
internal sealed record WaitSignalCommand(long JobId, CheckpointKindCode Kind, string Name, int? TimeoutSeconds);
