namespace Acta;

/// <summary>
/// One step of a job: its name, its status, the failure that ended an at-most-once step, and when the step was first
/// started and last written. The step's memoized result stays the engine's.
/// </summary>
/// <param name="Name">The step's name, unique within its job.</param>
/// <param name="Status">The step's status.</param>
/// <param name="FailureMessage">Why an at-most-once step ended failed or interrupted; null for every other step.</param>
/// <param name="CreatedAtUtc">When the row was created, in UTC.</param>
/// <param name="ModifiedAtUtc">When the row last changed, in UTC.</param>
public sealed record ListJobStepsItem(
    string Name,
    StepStatusCode Status,
    string? FailureMessage,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc
);
