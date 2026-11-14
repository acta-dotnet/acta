namespace Acta;

/// <summary>The job whose stored input <c>Input</c> replaces, compare-and-set when <c>ExpectedVersion</c> is set.</summary>
internal sealed record UpdateJobInputCommand(JobLookup Job, JobPayload Input, string? ReasonMessage, string? Actor, long? ExpectedVersion);
