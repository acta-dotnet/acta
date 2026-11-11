namespace Acta;

/// <summary>
/// Routine outcome for one <c>complete_job_step</c> call; mirrors the SQL <c>outcome_code</c>.
/// </summary>
internal enum CompleteJobStepOutcomeCode : byte
{
    /// <summary>Body succeeded; the step is terminal <c>Succeeded</c> with the stored result.</summary>
    Succeeded = 1,

    /// <summary>An at-most-once body failed; the step is terminal <c>Failed</c>.</summary>
    Failed = 3,

    /// <summary>
    /// The version CAS matched no row: the step advanced under a concurrent execution of the same job,
    /// so this attempt no longer owns it. Nothing was written.
    /// </summary>
    StaleVersion = 4,
}
