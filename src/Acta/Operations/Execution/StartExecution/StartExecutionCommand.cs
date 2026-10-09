namespace Acta;

/// <summary>The start: the claim's job, worker, and execution number, and the lease to stamp.</summary>
internal sealed record StartExecutionCommand(long JobId, int WorkerId, int ExpectedExecutionNumber, int LeaseTtlSeconds);
