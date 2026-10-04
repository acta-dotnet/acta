namespace Acta.Runtime;

/// <summary>An operator's raise of a named user signal on a job, with its payload.</summary>
internal sealed record RaiseSignalCommand(JobLookup Job, string Name, byte ValueFormatId, byte[]? Value, string? ActorKey);
