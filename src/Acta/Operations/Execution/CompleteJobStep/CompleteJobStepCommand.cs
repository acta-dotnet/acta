namespace Acta;

/// <summary>
/// Validated <c>complete_job_step</c> inputs: the attempt's fence and the step's name, the outcome and result payload,
/// the failure of an at-most-once body, and the completion CAS token <c>ExpectedVersion</c>.
/// </summary>
internal sealed record CompleteJobStepCommand(
    ExecutionFence Fence,
    string Name,
    bool Succeeded,
    byte ResultFormatId,
    byte[]? Result,
    StepFailureCode? FailureCode,
    string? FailureMessage,
    int ExpectedVersion
);
