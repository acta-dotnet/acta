namespace Acta;

internal enum CompleteExecutionsAction : byte
{
    Completed = 1,
    NotOwner = 2,
    AlreadyTerminal = 3,
    PerJob = 4,
}

/// <summary>
/// One completion's answer, keyed by its job and execution number: <c>Action</c> says whether the batch completed the
/// attempt, lost ownership, found the job terminal or moved past this execution, or left the job to
/// <see cref="ExecutionOperations.CompleteExecutionAsync"/> because it has a parent or a lane. <c>FinalStatusCode</c> is the row
/// after the call, null when the row is gone.
/// </summary>
[DbProjection]
internal sealed record CompleteExecutionsRow(
    long JobId,
    int ExecutionNumber,
    CompleteExecutionsAction Action,
    byte? FinalStatusCode,
    DateTime DbNowUtc
);
