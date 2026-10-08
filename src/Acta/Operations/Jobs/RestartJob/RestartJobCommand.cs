namespace Acta;

/// <summary>The job to restart, compare-and-set when <c>ExpectedVersion</c> is set.</summary>
internal sealed record RestartJobCommand(JobLookup Job, string? ReasonMessage, string? ActorKey, int? ExpectedVersion);
