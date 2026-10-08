namespace Acta.Runtime;

/// <summary>One note under the attempt that wrote it, with an optional detail payload.</summary>
internal sealed record RecordNoteCommand(long JobId, int ExecutionNumber, string Message, JobPayload? Detail);
