namespace Acta;

/// <summary>
/// Routine outcome for one <c>start_job_step</c> call; mirrors the SQL <c>outcome_code</c>.
/// </summary>
internal enum StartJobStepOutcomeCode : byte
{
    /// <summary>Run the body: a first start, or an ordinary step a replay found started with no success recorded.</summary>
    Invoke = 1,

    /// <summary>Already succeeded; return the stored result without running the body.</summary>
    ReplaySuccess = 3,

    /// <summary>An at-most-once body already failed; throw <c>StepExhaustedException</c>.</summary>
    Exhausted = 4,

    /// <summary>An at-most-once step was re-entered before completion; the row was terminalized
    /// <c>Interrupted</c>. Do not run the body; throw <c>StepInterruptedException</c>.</summary>
    Interrupted = 5,
}
