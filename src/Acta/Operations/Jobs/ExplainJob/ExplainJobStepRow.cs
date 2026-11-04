namespace Acta;

/// <summary>One <c>steps</c> row for the explain read, bound by name from a row_kind 2 row.</summary>
[DbProjection(ByName = true)]
internal sealed record ExplainJobStepRow(
    string StepName,
    StepStatusCode StepStatus,
    int AttemptNumber,
    DateTime? NextRetryAtUtc,
    string? FailureMessage
);
