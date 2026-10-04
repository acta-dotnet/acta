namespace Acta.Runtime;

/// <summary>One note under the attempt that wrote it, with an optional detail payload.</summary>
internal sealed record RecordJobNoteCommand(long JobId, int ExecutionNumber, string Message, JobPayload? Detail);
