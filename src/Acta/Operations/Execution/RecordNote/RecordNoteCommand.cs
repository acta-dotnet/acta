namespace Acta;

/// <summary>One note under the attempt and the worker that wrote it, with an optional detail payload.</summary>
internal sealed record RecordNoteCommand(long JobId, int ExecutionNumber, int WorkerId, string Message, JobPayload? Detail);
