namespace Acta.Runtime;

internal enum CompleteExecutionAction : byte
{
    Completed = 1,
    NotOwner = 2,
    AlreadyTerminal = 3,
}

/// <summary>
/// Completion outcome plus the job's resulting runtime state, read in the same round trip. <c>Action</c> says whether
/// this call completed the attempt, lost ownership, or found the job terminal; <c>FinalStatusCode</c> and
/// <c>FinalNextRunAtUtc</c> are the row after this call (what this completion produced, or the current row on
/// NotOwner and AlreadyTerminal; null when the row is gone), compared against <c>DbNowUtc</c> with no host-clock
/// assumption; <c>ParentReleased</c> means a terminal landing flipped a Suspended parent to Ready, waking every worker
/// namespace since the parent may live elsewhere, and <c>LanePromoted</c> that the lane's next member is now Ready.
/// </summary>
[DbProjection]
internal sealed record CompleteExecutionResponse(
    CompleteExecutionAction Action,
    byte? FinalStatusCode,
    DateTime? FinalNextRunAtUtc,
    DateTime DbNowUtc,
    bool ParentReleased,
    bool LanePromoted = false
);
