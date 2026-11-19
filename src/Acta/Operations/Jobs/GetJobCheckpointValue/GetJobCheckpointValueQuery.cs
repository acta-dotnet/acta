namespace Acta;

/// <summary>The job whose checkpoint to read, and the checkpoint's kind and name as the checkpoint list shows them.</summary>
internal sealed record GetJobCheckpointValueQuery(JobLookup Job, CheckpointKindCode Kind, string Name);
