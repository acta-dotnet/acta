namespace Acta;

/// <summary>The terminal job to delete.</summary>
internal sealed record PurgeJobCommand(JobLookup Job, string? ReasonMessage, string? Actor);
