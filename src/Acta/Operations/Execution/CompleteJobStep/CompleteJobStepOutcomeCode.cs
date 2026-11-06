namespace Acta;

/// <summary>
/// Routine outcome for one <c>complete_job_step</c> call; mirrors the SQL <c>outcome_code</c>.
/// </summary>
internal enum CompleteJobStepOutcomeCode : byte
{
    /// <summary>Body succeeded; the step is terminal <c>Succeeded</c> with the stored result.</summary>
    Succeeded = 1,

    /// <summary>Body failed in budget; the step stays <c>Pending</c> with <c>next_retry_at_utc</c> set.</summary>
    RetryScheduled = 2,

    /// <summary>Body failed and the budget is spent; the step is terminal <c>Exhausted</c>.</summary>
    Exhausted = 3,

    /// <summary>
    /// The version CAS matched no row: the step advanced under a concurrent execution of the same job,
    /// so this attempt no longer owns it. Nothing was written.
    /// </summary>
    StaleVersion = 4,
}
