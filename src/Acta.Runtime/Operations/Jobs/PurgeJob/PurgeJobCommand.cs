namespace Acta.Runtime;

/// <summary>The terminal job to delete.</summary>
internal sealed record PurgeJobCommand(JobLookup Job, string? ActorKey);
