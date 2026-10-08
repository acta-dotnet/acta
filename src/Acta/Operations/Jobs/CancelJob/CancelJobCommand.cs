namespace Acta;

/// <summary>The job to cancel, compare-and-set when <c>ExpectedVersion</c> is set.</summary>
internal sealed record CancelJobCommand(JobLookup Job, string? ReasonMessage, string? ActorKey, int? ExpectedVersion);
