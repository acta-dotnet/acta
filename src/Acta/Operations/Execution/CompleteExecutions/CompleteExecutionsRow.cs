namespace Acta;

internal enum CompleteExecutionsAction : byte
{
    Completed = 1,
    NotOwner = 2,
    AlreadyTerminal = 3,
}

/// <summary>
/// One completion's answer, keyed by its job and execution number. <c>Action</c> says whether the batch completed the
/// attempt, lost ownership, or found the job terminal or moved past this execution. <c>FinalStatusCode</c> and
/// <c>FinalNextRunAtUtc</c> are the row after the call (null when the row is gone), compared against <c>DbNowUtc</c> with
/// no host-clock assumption; <c>ParentReleased</c> means a terminal landing flipped a Suspended parent to Ready, waking
/// every worker namespace since the parent may live elsewhere, and <c>LanePromoted</c> that the lane's next member is now Ready.
/// </summary>
[DbProjection]
internal sealed record CompleteExecutionsRow(
    long JobId,
    int ExecutionNumber,
    CompleteExecutionsAction Action,
    byte? FinalStatusCode,
    DateTime? FinalNextRunAtUtc,
    DateTime DbNowUtc,
    bool ParentReleased,
    bool LanePromoted
);
