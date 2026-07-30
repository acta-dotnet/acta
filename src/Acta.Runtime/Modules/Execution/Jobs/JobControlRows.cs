namespace Acta.Modules.Execution.Jobs;

/// <summary>
/// Result of a cancel attempt: the control outcome plus the row's parent id, so the caller can raise
/// the child-done latch on the parent after the cancel commits.
/// </summary>
internal sealed record CancelJobOutcome(JobControlOutcome Outcome, long? ParentId);

/// <summary>
/// Flat cancel routine row; wraps the shared control outcome after binding.
/// </summary>
internal readonly record struct CancelJobOutcomeRow(JobControlActionInternal Action, JobStatusCode? Status, long? ParentId)
{
    public CancelJobOutcome ToOutcome() => new(new JobControlOutcome(Action, Status), ParentId);
}
