namespace Acta;

/// <summary>
/// One step of a job: its name, its status, the attempt it is on, when a pending retry is due, the last failure's
/// message, and when the step was first started and last written. The step's memoized result stays the engine's.
/// </summary>
/// <param name="Name">The step's name, unique within its job.</param>
/// <param name="Status">The step's status.</param>
/// <param name="AttemptNumber">The attempt the step is on, starting at 1.</param>
/// <param name="NextRetryAtUtc">When a pending retry of the step is due, in UTC; null when none is pending.</param>
/// <param name="FailureMessage">The last failure's message; null when the step has not failed.</param>
/// <param name="CreatedAtUtc">When the row was created, in UTC.</param>
/// <param name="ModifiedAtUtc">When the row last changed, in UTC.</param>
public sealed record ListJobStepsItem(
    string Name,
    StepStatusCode Status,
    int AttemptNumber,
    DateTime? NextRetryAtUtc,
    string? FailureMessage,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc
);
