using Acta.Runtime.Modules.Execution.Api;

namespace Acta.Runtime.Modules.Execution.Jobs;

/// <summary>
/// Result of a cancel attempt: the control outcome plus the row's parent id, so the caller can raise
/// the child-done latch on the parent after the cancel commits, and whether the cancel promoted the
/// next member of the job's lane, which the caller announces to the claim loops.
/// </summary>
internal sealed record CancelJobOutcome(JobControlOutcome Outcome, long? ParentId, bool LanePromoted);

/// <summary>
/// Flat cancel routine row; wraps the shared control outcome after binding.
/// </summary>
internal readonly record struct CancelJobOutcomeRow(
    JobControlActionInternal Action,
    JobStatusCode? Status,
    long? ParentId,
    int? Version,
    bool LanePromoted
)
{
    public CancelJobOutcome ToOutcome() => new(new JobControlOutcome(Action, Status, Version), ParentId, LanePromoted);
}
