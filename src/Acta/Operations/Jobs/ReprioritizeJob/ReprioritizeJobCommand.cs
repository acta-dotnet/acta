namespace Acta;

/// <summary>The job whose next claim takes <c>Priority</c>, compare-and-set when <c>ExpectedVersion</c> is set.</summary>
internal sealed record ReprioritizeJobCommand(
    JobLookup Job,
    JobPriorityCode Priority,
    string? ReasonMessage,
    string? Actor,
    int? ExpectedVersion
);
