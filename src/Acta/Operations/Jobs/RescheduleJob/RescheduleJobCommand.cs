namespace Acta;

/// <summary>The job to move to <c>NextRunAtUtc</c>, compare-and-set when <c>ExpectedVersion</c> is set.</summary>
internal sealed record RescheduleJobCommand(
    JobLookup Job,
    DateTime NextRunAtUtc,
    string? ReasonMessage,
    string? ActorKey,
    int? ExpectedVersion
);
