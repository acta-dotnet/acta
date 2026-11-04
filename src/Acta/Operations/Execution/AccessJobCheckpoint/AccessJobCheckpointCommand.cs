namespace Acta;

/// <summary>
/// One validated <c>access_job_checkpoint</c> call; a write action carries the payload columns. Every action binds the fence,
/// and the write actions check it.
/// </summary>
internal sealed record AccessJobCheckpointCommand(
    AccessJobCheckpointAction Action,
    ExecutionFence Fence,
    CheckpointKindCode Kind,
    string Name,
    byte ValueFormatId,
    byte[]? Value
);
