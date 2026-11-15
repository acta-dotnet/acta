namespace Acta;

/// <summary>An operator's clear of a named user signal from a finished job or a permanent job between firings.</summary>
internal sealed record ClearJobSignalCommand(JobLookup Job, string Name, string? Actor);
