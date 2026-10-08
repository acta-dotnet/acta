namespace Acta;

/// <summary>One <c>steps</c> row for the explain read, in SELECT order.</summary>
[DbProjection]
internal sealed record GetJobExplanationStepRow(
    string Name,
    StepStatusCode Status,
    int AttemptNumber,
    DateTime? NextRetryAtUtc,
    string? ReasonMessage
);
