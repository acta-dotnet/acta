namespace Acta;

/// <summary>One note under the attempt and the worker that wrote it, with an optional detail payload.</summary>
internal sealed record RecordJobNoteCommand(ExecutionFence Fence, string Message, JobPayload? Detail);
