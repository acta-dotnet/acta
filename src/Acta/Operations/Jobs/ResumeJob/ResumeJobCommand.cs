namespace Acta;

/// <summary>The job to resume, compare-and-set when <c>ExpectedVersion</c> is set.</summary>
internal sealed record ResumeJobCommand(JobLookup Job, string? ReasonMessage, string? ActorKey, int? ExpectedVersion);
