namespace Acta;

/// <summary>The start CAS: the claim's job, worker, execution number, and version, and the lease to stamp.</summary>
internal sealed record StartExecutionCommand(
    long JobId,
    int WorkerId,
    int ExpectedExecutionNumber,
    int ExpectedVersion,
    int LeaseTtlSeconds
);
