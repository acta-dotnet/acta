namespace Acta;

/// <summary>
/// One step slot of a job: its name, its status, the attempt it is on, when a pending retry is due, the last failure's
/// message, and when the slot was first started and last written. The step's memoized result stays the engine's.
/// </summary>
public sealed record ListJobStepsItem(
    string Name,
    StepStatusCode Status,
    int AttemptNumber,
    DateTime? NextRetryAtUtc,
    string? FailureMessage,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc
);
