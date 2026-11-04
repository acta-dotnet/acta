namespace Acta;

/// <summary>An operator's raise of a named user signal on a job; <see cref="JobPayload.None"/> raises presence only.</summary>
internal sealed record RaiseJobSignalCommand(JobLookup Job, string Name, JobPayload Value, string? Actor);
