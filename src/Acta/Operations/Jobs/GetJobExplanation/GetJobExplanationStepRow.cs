namespace Acta;

/// <summary>One <c>steps</c> row for the explain read, in SELECT order.</summary>
[DbProjection]
internal sealed record GetJobExplanationStepRow(
    string StepName,
    StepStatusCode Status,
    int AttemptNumber,
    DateTime? NextRetryAtUtc,
    string? FailureMessage
);
