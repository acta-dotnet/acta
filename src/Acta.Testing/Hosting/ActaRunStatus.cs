namespace Acta.Testing;

/// <summary>
/// Coarse outcome of one <see cref="IActaTestHost.RunOnceAsync"/> tick: the public mirror of the
/// runtime's internal run outcome, so integrators can assert on what a single drive did.
/// </summary>
public enum ActaRunStatus : byte
{
    /// <summary>Nothing was claimable this tick (no Ready job, or a claim lost its row before start).</summary>
    NothingClaimed = 1,

    /// <summary>
    /// A job ran to terminal <c>Succeeded</c>, or the run was cancelled; a recurring slot's success leaves it
    /// <c>Ready</c> for its next occurrence.
    /// </summary>
    Completed = 2,

    /// <summary>A job ran and settled terminal <c>Failed</c>: its budget spent, or a non-retryable exception.</summary>
    Failed = 3,

    /// <summary>
    /// The job re-armed for a later claim: it rescheduled, slept, paused, suspended on a signal, or threw
    /// with budget left (that retry spends budget), or a concurrency or rate limit bounced it.
    /// </summary>
    Rearmed = 4,
}
