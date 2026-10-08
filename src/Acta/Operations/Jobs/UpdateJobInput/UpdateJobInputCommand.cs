namespace Acta;

/// <summary>The job whose stored input <c>Input</c> replaces.</summary>
internal sealed record UpdateJobInputCommand(JobLookup Job, JobPayload Input, string? ReasonMessage, string? ActorKey);
