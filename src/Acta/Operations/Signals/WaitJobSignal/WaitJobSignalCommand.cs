namespace Acta;

/// <summary>
/// The checkpoint an attempt waits on under its fence, and the wait's bound: a length in database-clock seconds or an absolute
/// instant, at most one of them (neither is unbounded).
/// </summary>
internal sealed record WaitJobSignalCommand(
    ExecutionFence Fence,
    CheckpointKindCode Kind,
    string Name,
    int? TimeoutSeconds,
    DateTime? DueAtUtc
);
