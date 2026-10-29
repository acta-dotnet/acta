namespace Acta;

/// <summary>The job to move to <c>NextRunAtUtc</c>, now when null, compare-and-set when <c>ExpectedVersion</c> is set.</summary>
internal sealed record RescheduleJobCommand(
    JobLookup Job,
    DateTime? NextRunAtUtc,
    string? ReasonMessage,
    string? Actor,
    int? ExpectedVersion
);
