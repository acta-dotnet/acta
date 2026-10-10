namespace Acta;

/// <summary>The job to pause, compare-and-set when <c>ExpectedVersion</c> is set.</summary>
internal sealed record PauseJobCommand(JobLookup Job, string? ReasonMessage, string? Actor, int? ExpectedVersion);
